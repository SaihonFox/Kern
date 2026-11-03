using Kern.Bytecode;
using Kern.Vm.Async;
using Kern.Vm.Native;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>
/// Интерпретатор модуля байткода. Один экземпляр — одна VM: свой планировщик, свой стек,
/// общая с <see cref="Fork"/> таблица типов и статики. Кадры не используют стек C#:
/// при <c>await</c> состояние (<see cref="ExecutionState"/>) паркуется в задаче и
/// продолжается планировщиком в потоке-владельце.
///
/// Контракт (spec §16): каждое выражение оставляет одно значение, <c>Ret</c> снимает
/// ровно один результат, <c>Call</c> заменяет аргументы результатом на том же месте стека.
/// </summary>
public sealed partial class Interpreter
{
	private const int InitialStack = 64;
	private const int InitialFrames = 64;
	/// <summary>Предел глубины кадров по умолчанию (spec: 10 000, настраивается).</summary>
	public const int DefaultMaxCallDepth = 10_000;

	private static readonly TimeSpan AsyncWaitTimeout = TimeSpan.FromSeconds(30);
	private readonly int _maxCallDepth = ReadMaxCallDepth();

	/// <summary>
	/// Предел глубины кадров этого интерпретатора. Задаётся переменной окружения
	/// <c>KERN_MAX_CALL_DEPTH</c> (положительное целое) при создании VM.
	/// </summary>
	public int MaxCallDepth => _maxCallDepth;

	private static int ReadMaxCallDepth()
	{
		string? text = Environment.GetEnvironmentVariable("KERN_MAX_CALL_DEPTH");
		return int.TryParse(text, out int value) && value > 0 ? value : DefaultMaxCallDepth;
	}

	private readonly KbcModule _module;
	private readonly TypeFactory _types;
	private readonly INativeResolver? _natives;
	private readonly KValue[] _statics;
	private readonly StaticInitState _staticInit;
	private readonly Scheduler _scheduler;
	private readonly Dictionary<string, NativeBinding> _nativeCache = [];

	public Interpreter(KbcModule module)
		: this(module, null)
	{
	}

	public Interpreter(KbcModule module, INativeResolver? natives)
	{
		ArgumentNullException.ThrowIfNull(module);
		KbcModuleValidator.Validate(module);
		_module = module;
		_types = new TypeFactory(module);
		_natives = natives;
		_scheduler = new Scheduler();
		_statics = new KValue[module.FieldPool.Count];
		Array.Fill(_statics, KValue.None);
		_staticInit = new StaticInitState();
	}

	private Interpreter(
		KbcModule module,
		TypeFactory types,
		INativeResolver? natives,
		KValue[] statics,
		StaticInitState staticInit)
	{
		_module = module;
		_types = types;
		_natives = natives;
		_scheduler = new Scheduler();
		_statics = statics;
		_staticInit = staticInit;
	}


	public KbcModule Module => _module;

	public TypeFactory Types => _types;

	public Scheduler Scheduler => _scheduler;

	/// <summary>Профилировщик исполнения; включается <c>kern run --profile</c>.</summary>
	public Profiler? Profiler { get; set; }

	/// <summary>Хук отладчика (<c>kernc debug</c>); <c>null</c> — обычное исполнение.</summary>
	public IDebugHook? Debugger { get; set; }

	/// <summary>
	/// Новый интерпретатор для другого потока: общие модуль, типы и статика, собственные
	/// планировщик, стек и кадры (DECISIONS §D, общая память).
	/// </summary>
	public Interpreter Fork() => new(_module, _types, _natives, _statics, _staticInit);

	/// <summary>Выполнить функцию по имени и числу аргументов (перегрузки различаются арностью).</summary>
	public KValue Run(string functionName, params KValue[] args)
	{
		ArgumentNullException.ThrowIfNull(functionName);
		args ??= [];
		if (!_module.TryFindFunction(functionName, args.Length, out int index))
			throw new KernPanic(
				$"функция '{functionName}' с {args.Length} аргументами не найдена в модуле '{_module.Name}'");
		return Run(index, args);
	}

	/// <summary>Выполнить функцию по индексу в пуле модуля.</summary>
	public KValue Run(int functionIndex, KValue[] args)
	{
		ArgumentNullException.ThrowIfNull(args);
		if ((uint)functionIndex >= (uint)_module.FunctionPool.Count)
			throw new KernPanic($"индекс функции вне пула: {functionIndex}");

		FunctionDef entry = _module.FunctionPool[functionIndex];
		if (args.Length != entry.ParamCount)
			throw new KernPanic($"функция '{entry.Name}' ожидает {entry.ParamCount} аргументов, передано {args.Length}");

		EnsureStaticInitialized();
		ExecutionState state = CreateState(entry, args, null);
		return ExecuteToCompletion(state);
	}

	/// <summary>
	/// Вызвать функцию-значение (функцию или замыкание) — используется нативами
	/// (<c>list.map</c>, комбинаторы задач) и тестами.
	/// </summary>
	public KValue RunValue(KValue callable, params KValue[] args)
	{
		args ??= [];
		EnsureStaticInitialized();
		ExecutionState state = callable.Ref switch
		{
			KClosure closure => CreateState(closure.Function, args, closure.Captures),
			FunctionDef { IsNative: true } native => throw new KernPanic(
				$"RunValue: нативную функцию '{native.Name}' нужно вызывать через нативную привязку"),
			FunctionDef function => CreateState(function, args, null),
			_ => throw new KernPanic($"RunValue: ожидалась функция, получено {callable.TypeName}"),
		};
		return ExecuteToCompletion(state);
	}

	/// <summary>
	/// Синоним <see cref="RunValue(KValue, KValue[])"/> для рантайма: выполнить
	/// функцию-значение в этой VM (в том числе в форке рабочего потока).
	/// </summary>
	public KValue CallFunction(KValue callable, params KValue[] args) => RunValue(callable, args);

	/// <summary>
	/// Запустить функцию-значение как отдельную задачу в этой VM (spec round 23):
	/// корутина выполняется до первого <c>await</c>, дальше её ведёт планировщик.
	/// Нужен комбинаторам (<c>tasks.run</c>, <c>tasks.retry</c>) и пулам.
	/// </summary>
	public KTask StartAsync(KValue callable, params KValue[] args)
	{
		args ??= [];
		EnsureStaticInitialized();
		ExecutionState state = callable.Ref switch
		{
			KClosure closure => CreateState(closure.Function, args, closure.Captures),
			FunctionDef { IsNative: true } native => throw new KernPanic(
				$"StartAsync: нативную функцию '{native.Name}' нельзя запустить как задачу"),
			FunctionDef function => CreateState(function, args, null),
			_ => throw new KernPanic($"StartAsync: ожидалась функция, получено {callable.TypeName}"),
		};
		var task = new KTask();
		state.CompletionTask = task;
		Pump(state);
		return task;
	}

	/// <summary>
	/// Создать ленивый генератор для функции с флагом <see cref="MemberFlags.Generator"/>:
	/// тело не исполняется до первого <see cref="KGenerator.TryNext"/>.
	/// </summary>
	public KGenerator CreateGenerator(FunctionDef function, KValue[] arguments, KValue[]? captures = null)
	{
		ArgumentNullException.ThrowIfNull(function);
		ArgumentNullException.ThrowIfNull(arguments);
		EnsureStaticInitialized();
		return new KGenerator(this, CreateState(function, arguments, captures));
	}

	/// <summary>Продолжить генератор до следующего <c>Yield</c> или конца тела.</summary>
	private bool GeneratorNext(KGenerator generator, out KValue value)
	{
		ExecutionState state = generator.State;
		value = KValue.None;
		if (state.Completed)
		{
			if (state.Error is { } completedError)
				throw completedError;
			return false;
		}

		state.HasYielded = false;
		state.Yielded = KValue.None;
		Pump(state);
		if (state.HasYielded)
		{
			value = state.Yielded;
			return true;
		}

		if (state.Error is { } error)
			throw error;
		if (!state.Completed)
			throw new KernPanic("генератор приостановлен не на yield");
		return false;
	}

	/// <summary>
	/// Ленивая последовательность из генератора: состояние исполнения паркуется на
	/// <see cref="OpCode.Yield"/> и возобновляется следующим <c>next()</c>.
	/// </summary>
	public sealed class KGenerator
	{
		private readonly Interpreter _interpreter;

		internal KGenerator(Interpreter interpreter, ExecutionState state)
		{
			_interpreter = interpreter;
			State = state;
		}

		internal ExecutionState State { get; }

		/// <summary>Следующий элемент; <c>false</c> — генератор завершился.</summary>
		public bool TryNext(out KValue value) => _interpreter.GeneratorNext(this, out value);
	}

	/// <summary>
	/// Выполнить точку входа модуля (main без аргументов).</summary>
	public KValue RunEntry()	{
		if (_module.EntryPoint < 0)
			throw new KernPanic($"в модуле '{_module.Name}' нет точки входа");
		return Run(_module.EntryPoint, []);
	}

	/// <summary>
	/// Выполнить <c>&lt;static-init&gt;</c> модуля ровно один раз, до пользовательской функции
	/// (spec §12: статическая инициализация библиотеки идёт раньше программы).
	/// </summary>
	/// <remarks>
	/// Ошибка инициализатора «залипает», как <c>TypeInitializationException</c> в C#: повторный
	/// <see cref="Run(int, KValue[])"/> не запускает инициализатор снова, а сразу бросает
	/// сохранённую ошибку. Иначе частично записанная статика осталась бы в неопределённом
	/// состоянии, а побочные эффекты инициализатора повторились бы.
	/// </remarks>
	private void EnsureStaticInitialized()
	{
		if (_module.StaticInit < 0) return;

		StaticInitState state = _staticInit;
		int status = Volatile.Read(ref state.Status);
		if (status == StaticInitState.Done) return;
		if (status == StaticInitState.Failed)
		{
			ThrowStickyStaticInit(state);
			return;
		}
		// Инициализатор уже выполняется: это реентерабельный Run с того же потока
		// (натив инициализатора вызвал Run) — второй раз его не запускаем.
		if (status == StaticInitState.Running) return;

		if (Interlocked.CompareExchange(
				ref state.Status, StaticInitState.Running, StaticInitState.Pending) != StaticInitState.Pending)
		{
			// Запуск выиграл другой поток. По DECISIONS §D (раунд 24) синхронизация общих
			// данных — ответственность программы, поэтому не ждём чужой инициализатор:
			// если он уже упал, отдаём залипшую ошибку, иначе продолжаем (статика может
			// быть ещё не дописана — это гонка самой программы).
			if (Volatile.Read(ref state.Status) == StaticInitState.Failed)
				ThrowStickyStaticInit(state);
			return;
		}

		try
		{
			// Аргументов нет, результат отбрасывается: у <static-init> нет языковой
			// сигнатуры, он только заполняет статические слоты.
			_ = ExecuteToCompletion(CreateState(_module.FunctionPool[_module.StaticInit], [], null));
		}
		catch (Exception error)
		{
			state.Error = error;
			Volatile.Write(ref state.Status, StaticInitState.Failed);
			throw;
		}

		Volatile.Write(ref state.Status, StaticInitState.Done);
	}

	/// <summary>Бросить сохранённую ошибку статической инициализации, не повторяя её.</summary>
	private static void ThrowStickyStaticInit(StaticInitState state) =>
		throw state.Error ?? new KernPanic("статическая инициализация модуля завершилась ошибкой");

	private ExecutionState CreateState(FunctionDef entry, KValue[] args, KValue[]? captures)
	{
		if (args.Length != entry.ParamCount)
			throw new KernPanic($"функция '{entry.Name}' ожидает {entry.ParamCount} аргументов, передано {args.Length}");
		KValue[] locals = new KValue[Math.Max(entry.LocalCount, entry.ParamCount)];
		Array.Copy(args, locals, args.Length);
		return new ExecutionState(this)
		{
			Function = entry,
			Code = entry.Code,
			Locals = locals,
			Captures = captures,
			Stack = new OperandStack(InitialStack),
			Frames = new Frame[InitialFrames],
			FrameCount = 0,
			Pc = 0,
			StackBase = 0,
		};
	}

	/// <summary>Довести состояние до завершения: крутим планировщик, ждём внешние события.</summary>
	private KValue ExecuteToCompletion(ExecutionState state)
	{
		Pump(state);
		long startTicks = Environment.TickCount64;
		while (!state.Completed)
		{
			if (_scheduler.HasWork)
			{
				_scheduler.RunUntilIdle();
				continue;
			}
			if (Environment.TickCount64 - startTicks > (long)AsyncWaitTimeout.TotalMilliseconds)
				throw new KernPanic("асинхронная операция не завершилась: нет внешних событий");
			_scheduler.WaitForWork(TimeSpan.FromMilliseconds(50));
		}

		if (state.Error is { } error) throw error;
		return state.Result;
	}

	/// <summary>Выполнять инструкции, пока состояние не завершится или не припаркуется на <c>await</c>.</summary>
	private void Pump(ExecutionState state)
	{
		try
		{
			RunLoop(state);
		}
		catch (KernPanic panic)
		{
			state.Fail(panic);
		}
		finally
		{
			if (state.Completed)
				CompleteTask(state);
		}
	}

	/// <summary>
	/// Завершить задачу, запущенную через <see cref="StartAsync"/>: значение
	/// асинхронной функции может быть вложенной <c>task&lt;T&gt;</c> — её разворачиваем.
	/// </summary>
	private void CompleteTask(ExecutionState state)
	{
		KTask? task = state.CompletionTask;
		if (task is null || task.IsCompleted)
			return;

		if (state.Error is { } error)
		{
			task.Fail(error);
			return;
		}

		if (state.Result.Ref is KTask inner && !ReferenceEquals(inner, task))
		{
			inner.OnCompleted(completed =>
			{
				if (completed.Status == KTaskStatus.Succeeded)
					task.Complete(completed.Result);
				else if (completed.Status == KTaskStatus.Canceled)
					task.Cancel();
				else
					task.Fail(completed.Error ?? new KernPanic("задача завершилась ошибкой"));
			}, _scheduler);
			return;
		}

		task.Complete(state.Result);
	}


	/// <summary>Продолжить припаркованное состояние после завершения задачи.</summary>
	private void Resume(ExecutionState state, KTask task)
	{
		if (state.Completed) return;

		if (task.Status == KTaskStatus.Failed)
		{
			KernPanic error = task.Error ?? new KernPanic("задача завершилась ошибкой");
			error.AddFrame(state.Function.Name, state.Pc, state.Function.LineAt(state.Pc));
			// Конструктор unit! мог припарковаться на await: его окно создания записано
			// в кадре вызывающего и должно быть снято вместе с раскруткой состояния.
			CloseConstructionWindows(state.Frames, state.FrameCount);
			state.Fail(error);
			// Resume не идёт через Pump, поэтому задачу состояния нужно завершить явно:
			// иначе внешний task<T> навсегда останется Pending (t.done == false).
			CompleteTask(state);
			return;
		}

		if (task.Status == KTaskStatus.Canceled)
		{
			KernPanic canceled = new("задача отменена");
			canceled.AddFrame(state.Function.Name, state.Pc, state.Function.LineAt(state.Pc));
			CloseConstructionWindows(state.Frames, state.FrameCount);
			state.Fail(canceled);
			CompleteTask(state);
			return;
		}

		OperandStack stack = state.Stack;
		stack.Push(task.Result);
		state.Stack = stack;
		Pump(state);
	}

	private static void PushTaskResult(KTask task, ref OperandStack stack)
	{
		if (task.Status == KTaskStatus.Succeeded)
		{
			stack.Push(task.Result);
			return;
		}

		if (task.Status == KTaskStatus.Canceled)
			throw new KernPanic("задача отменена");
		throw task.Error ?? new KernPanic("задача завершилась ошибкой");
	}

	/// <summary>Снять аргументы со стека и вызвать нативную привязку, положив результат на их место.</summary>
	private void InvokeNative(FunctionDef function, int argc, ref OperandStack stack)
	{
		KValue[] arguments = new KValue[argc];
		for (int i = argc - 1; i >= 0; i--)
			arguments[i] = stack.Pop();
		stack.Push(InvokeNative(function, arguments));
	}

	/// <summary>
	/// Вызов нативного члена через <see cref="INativeResolver"/>; результат кэшируется по
	/// <see cref="FunctionDef.NativeKey"/>. Reified-аргументы берутся из пула подписей.
	/// </summary>
	private KValue InvokeNative(FunctionDef function, KValue[] arguments)
	{
		if (_natives is null)
			throw new KernPanic(
				$"нативная функция '{function.Name}' не может быть вызвана: резолвер нативов не подключён");
		string key = function.NativeKey ?? throw new KernPanic(
			$"нативная функция '{function.Name}' без NativeKey");
		if (!_nativeCache.TryGetValue(key, out NativeBinding? binding))
		{
			if (!_natives.TryResolve(key, out binding))
				throw new KernPanic($"нативная привязка '{key}' не найдена среди загруженных сборок");
			_nativeCache[key] = binding;
		}
		if (binding.ParameterCount != 0 && binding.ParameterCount != arguments.Length)
			throw new KernPanic(
				$"нативная функция '{key}' ожидает {binding.ParameterCount} аргументов, передано {arguments.Length}");

		KType[] typeArguments = function.NativeTypeArgs >= 0
			? [.. _module.TypeSigListPool[function.NativeTypeArgs].Select(index => _types.FromSig(_module.TypeSigPool[index]))]
			: [];
		return TagNativeResult(binding.Invoke(this, arguments, typeArguments), function.ReturnType);
	}

	/// <summary>
	/// Нативный handle-тип (<c>streamwriter</c>, <c>xml_node</c>) получает reified-тип из
	/// объявленной подписи функции: без этого <c>is</c> видел бы только <c>object</c>.
	/// Мост <c>object</c> и примитивы не трогаем.
	/// </summary>
	private KValue TagNativeResult(KValue value, TypeSig signature)
	{
		if (value.Kind != ValueKind.Ref || value.Ref is not KNativeBox box || box.NativeType is not null)
			return value;
		TypeSig named = signature is TypeSig.NullableSig nullable ? nullable.Inner : signature;
		if (named is not TypeSig.NamedSig)
			return value;

		try
		{
			KType type = _types.FromSig(named);
			return KValue.Obj(new KNativeBox(box.Value) { NativeType = type });
		}
		catch (InvalidOperationException)
		{
			return value;
		}
	}

	private static KValue[] AllocateLocals(FunctionDef function) =>
		new KValue[Math.Max(function.LocalCount, function.ParamCount)];

	private static KValue[] PopItems(ref OperandStack stack, int count)
	{
		if (count < 0)
			throw new KernPanic($"отрицательное число элементов: {count}");
		KValue[] items = new KValue[count];
		for (int i = count - 1; i >= 0; i--)
			items[i] = stack.Pop();
		return items;
	}

	/// <summary>
	/// B5b1: отрицательный индекс считается с конца (spec round 27), поэтому <c>-1</c> —
	/// последний элемент. Дальнейшая проверка границ — в индексаторе <c>KList</c>/<c>KArray</c>
	/// (паника). Слишком большой отрицательный индекс остаётся отрицательным и паникует.
	/// </summary>
	private static int NormalizeIndex(int index, int count) => index < 0 ? index + count : index;

	/// <summary>
	/// Является ли значение типа <paramref name="actual"/> экземпляром
	/// <paramref name="expected"/> с учётом родителей из <c>TypeDef.Parents</c> (spec §8):
	/// объект наследника проходит проверку на родителя. Глубина ограничена защитой от
	/// испорченного/циклического пула типов.
	/// </summary>
	private bool IsInstanceOf(KType actual, KType expected, int depth = 0)
	{
		if (actual == expected)
			return true;
		if (depth >= 32 || actual is not KClassType @class || expected is not KClassType)
			return false;

		foreach (TypeSig parent in @class.Definition.Parents)
		{
			KType resolved;
			try
			{
				resolved = _types.FromSig(parent, @class);
			}
			catch (InvalidOperationException)
			{
				continue;
			}

			if (IsInstanceOf(resolved, expected, depth + 1))
				return true;
		}

		return false;
	}

	private KType InferType(KValue value) => value.Kind switch
	{		ValueKind.None => _types.Prim(PrimKind.None),
		ValueKind.Nil => _types.Prim(PrimKind.Nil),
		ValueKind.Void => _types.Prim(PrimKind.Void),
		ValueKind.Bool => _types.Prim(PrimKind.Bool),
		ValueKind.Char => _types.Prim(PrimKind.Char),
		ValueKind.Str => _types.Prim(PrimKind.Str),
		ValueKind.I8 => _types.Prim(PrimKind.I8),
		ValueKind.I16 => _types.Prim(PrimKind.I16),
		ValueKind.I32 => _types.Prim(PrimKind.I32),
		ValueKind.I64 => _types.Prim(PrimKind.I64),
		ValueKind.U8 => _types.Prim(PrimKind.U8),
		ValueKind.U16 => _types.Prim(PrimKind.U16),
		ValueKind.U32 => _types.Prim(PrimKind.U32),
		ValueKind.U64 => _types.Prim(PrimKind.U64),
		ValueKind.F16 => _types.Prim(PrimKind.F16),
		ValueKind.F32 => _types.Prim(PrimKind.F32),
		ValueKind.F64 => _types.Prim(PrimKind.F64),
		ValueKind.Ref => value.Ref switch
		{
			KObject instance => instance.Type,
			KList list => _types.Builtin(BuiltinGenericKind.List, list.ElementType ?? _types.Prim(PrimKind.Object)),
			KArray array => _types.Builtin(BuiltinGenericKind.Array, array.ElementType ?? _types.Prim(PrimKind.Object)),
			KDict dict => _types.Builtin(
				BuiltinGenericKind.Dict,
				dict.KeyType ?? _types.Prim(PrimKind.Object),
				dict.ValueType ?? _types.Prim(PrimKind.Object)),
			KTuple tuple => _types.Tuple(tuple.ElementTypes),
			KClosure closure => FunctionType(closure.Function),
			FunctionDef function => FunctionType(function),
			KType type => type,
			KNativeBox { NativeType: { } nativeType } => nativeType,
			_ => _types.Prim(PrimKind.Object),
		},
		_ => _types.Prim(PrimKind.Object),
	};

	private KFuncType FunctionType(FunctionDef function) =>
		_types.Func(_types.FromSig(function.ReturnType), [.. function.ParamTypes.Select(_types.FromSig)]);

	private static KValue ToValue(KConst value) => value.Kind switch
	{
		KConstKind.Nil => KValue.Nil,
		KConstKind.None => KValue.None,
		KConstKind.Void => KValue.Void,
		KConstKind.Bool => KValue.Bool(value.AsBool),
		KConstKind.I64 => KValue.I64(value.AsI64),
		KConstKind.F64 => KValue.F64(value.AsF64),
		KConstKind.Char => KValue.Char(value.AsChar),
		KConstKind.Str => KValue.Str(value.AsStr),
		_ => throw new KernPanic($"неизвестный вид константы: {value.Kind}"),
	};

	/// <summary>
	/// Конструктор неизменяемого unit: его вызов открывает окно создания, в котором
	/// разрешена запись полей (spec §8/§9).
	/// <para>
	/// Инвариант окна: единственный опкод, который открывает окно, — <c>Call</c> в
	/// <c>Interpreter.RunLoop.cs</c> (вызов конструктора с получателем в слоте 0), и
	/// единственный штатный способ закрыть его — <c>Ret</c> того же вызова. Кадр,
	/// сохранённый этим <c>Call</c>, помнит открытый объект (<see cref="Frame.Construction"/>),
	/// поэтому раскрутка по панике закрывает окна тех кадров, которые не успели вернуться.
	/// </para>
	/// <para>
	/// Где окно может остаться открытым сегодня:
	/// <list type="bullet">
	/// <item><c>Stop</c> — завершает состояние, не снимая кадры; конструктор с <c>Stop</c>
	/// оставит объект записываемым. Кодогенератор <c>Stop</c> пока не выдаёт, путь
	/// достижим только вручную собранным байткодом.</item>
	/// <item><c>CallValue</c> — окно не открывает вовсе; конструкторы не являются
	/// first-class, поэтому вызвать их этим опкодом сейчас нельзя.</item>
	/// </list>
	/// Любой будущий путь, который создаёт immutable-unit вне <c>Call</c>, или переводит
	/// управление из конструктора не через <c>Ret</c>, обязан закрыть окно так же:
	/// записать объект в кадр вызова либо снять его в раскрутке
	/// (<see cref="CloseConstructionWindows"/>).
	/// </para>
	/// </summary>
	private bool IsImmutableUnitConstructor(FunctionDef function) =>
		(function.Flags & (MemberFlags.Ctor | MemberFlags.Static)) == MemberFlags.Ctor &&
		function.OwnerType >= 0 &&
		_module.TypePool[function.OwnerType].Kind == TypeDefKind.ImmutableUnit;

	/// <summary>
	/// Снять окна создания всех кадров <c>[0, count)</c>, которые ещё не вернулись:
	/// паника внутри конструктора не должна оставлять значение <c>unit!</c> записываемым.
	/// Обход от внутренних кадров к внешним — как раскрутка стека; после снятия ссылка
	/// в кадре обнуляется, чтобы повторная раскрутка не закрыла окно дважды.
	/// </summary>
	private static void CloseConstructionWindows(Frame[] frames, int count)
	{
		for (int i = count - 1; i >= 0; i--)
		{
			KObject? construction = frames[i].Construction;
			if (construction is null) continue;
			frames[i].Construction = null;
			construction.EndConstruction();
		}
	}

	private static bool RequireBool(KValue value) =>
		value.Kind == ValueKind.Bool ? value.AsBool : throw new KernPanic($"ожидалось bool, получено {value.TypeName}");

	private static long BitsToInt64(int low, int high) =>
		unchecked((long)(((ulong)(uint)high << 32) | (uint)low));


	/// <summary>
	/// Одноразовое состояние <c>&lt;static-init&gt;</c>, общее для всех форков: форки делят
	/// <c>_statics</c>, поэтому пользовательский инициализатор должен выполниться один раз
	/// на модуль, а не на каждый интерпретатор (DECISIONS §D, раунд 24).
	/// </summary>
	private sealed class StaticInitState
	{
		public const int Pending = 0;
		public const int Running = 1;
		public const int Done = 2;
		public const int Failed = 3;

		public int Status;
		public Exception? Error;
	}

	/// <summary>
	/// Припаркованное/текущее состояние выполнения одной корутины: стек, кадры и позиция.
	/// Хранится в <see cref="KTask"/> до продолжения — стек C# не удерживается.
	/// </summary>
	internal sealed class ExecutionState(Interpreter interpreter)
	{
		public Interpreter Interpreter { get; } = interpreter;
		public OperandStack Stack;
		public Frame[] Frames = [];
		public int FrameCount;
		public FunctionDef Function = null!;
		public int[] Code = [];
		public KValue[] Locals = [];
		public KValue[]? Captures;
		public int Pc;
		public int StackBase;
		public KValue Result = KValue.None;
		public KernPanic? Error;
		public bool Completed;

		/// <summary>Состояние припарковано на <c>Yield</c>: элемент ждёт в <see cref="Yielded"/>.</summary>
		public bool HasYielded;
		public KValue Yielded = KValue.None;

		/// <summary>Задача, которую надо завершить по окончании состояния (<see cref="StartAsync"/>).</summary>
		public KTask? CompletionTask;

		public void Complete(KValue result)
		{
			Result = result;
			Completed = true;
		}

		public void Fail(KernPanic error)
		{
			Error = error;
			Completed = true;
		}
	}

	/// <summary>Стек операндов кадра: массив значений + счётчик; растёт по необходимости.</summary>
	internal struct OperandStack
	{
		private KValue[] _items;
		public int Count;

		public OperandStack(int capacity)
		{
			_items = new KValue[Math.Max(capacity, 8)];
			Count = 0;
		}

		public void Push(KValue value)
		{
			if (Count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
			_items[Count++] = value;
		}

		public KValue Pop()
		{
			if (Count == 0) throw new KernPanic("пустой стек операндов");
			return _items[--Count];
		}

		public readonly KValue Peek()
		{
			if (Count == 0) throw new KernPanic("пустой стек операндов");
			return _items[Count - 1];
		}

		/// <summary>Значение по абсолютному индексу — для чтения получателя вызова.</summary>
		public readonly KValue At(int index) => _items[index];

		public void Swap()
		{
			if (Count < 2) throw new KernPanic("Swap: на стеке меньше двух значений");
			(_items[Count - 1], _items[Count - 2]) = (_items[Count - 2], _items[Count - 1]);
		}

		public void Truncate(int count) => Count = count;

		public readonly void CopyTo(int sourceIndex, KValue[] destination, int count) =>
			Array.Copy(_items, sourceIndex, destination, 0, count);
	}
}
