using Kern.Bytecode;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>
/// Динамический диспатч методов класса (spec §8) и общая настройка кадра вызова.
/// <para>
/// <c>Call</c> и <c>CallVirtual</c> обязаны идти одним путём: глубина вызовов, окна
/// создания <c>unit!</c> и баланс кадра, который проверяет <c>Ret</c>, не должны
/// расходиться. Поэтому оба опкода вызывают <see cref="BeginCall"/>.
/// </para>
/// <para>
/// Резолв виртуального вызова не кэшируется: модуль и <c>TypeFactory</c> общие для
/// <c>Interpreter.Fork()</c> (DECISIONS, раунд 24), а обход <c>TypeDef.Methods</c> и
/// <c>TypeDef.Parents</c> только читает неизменяемые данные.
/// </para>
/// </summary>
public sealed partial class Interpreter
{
	/// <summary>Предел обхода родителей: защита от испорченного <c>.kbc</c> с циклом.</summary>
	private const int MaxParentDepth = 32;

	/// <summary>
	/// Найти ближайшее переопределение метода по типу получателя: собственные методы
	/// <see cref="TypeDef.Methods"/>, затем родители с подставленными аргументами
	/// дженерика. Совпадение — имя и число параметров статической цели (spec §8:
	/// динамический диспатч по имени/арности). Нет совпадения — вызывается статическая цель.
	/// </summary>
	private FunctionDef ResolveVirtualTarget(FunctionDef target, KValue receiver)
	{
		if (receiver.Ref is not KObject instance || instance.Type is not KClassType runtime)
			return target;
		return FindVirtualMethod(runtime, target.Name, target.ParamCount, 0) ?? target;
	}

	private FunctionDef? FindVirtualMethod(KClassType type, string name, int parameterCount, int depth)
	{
		if (depth >= MaxParentDepth)
			return null;

		foreach (int index in type.Definition.Methods)
		{
			if ((uint)index >= (uint)_module.FunctionPool.Count)
				continue;
			FunctionDef candidate = _module.FunctionPool[index];
			if (candidate.Name != name || candidate.ParamTypes.Length != parameterCount)
				continue;
			if ((candidate.Flags & MemberFlags.Static) != 0)
				continue;
			return candidate;
		}

		foreach (TypeSig parent in type.Definition.Parents)
		{
			KType resolved;
			try
			{
				resolved = _types.FromSig(parent, type);
			}
			catch (InvalidOperationException)
			{
				continue;
			}

			if (resolved is KClassType baseType &&
				FindVirtualMethod(baseType, name, parameterCount, depth + 1) is { } found)
				return found;
		}

		return null;
	}

	/// <summary>
	/// Подготовить кадр вызова: проверки арности и базы, нативные вызовы, предел глубины,
	/// окно создания <c>unit!</c>, сохранение кадра вызывающего и перенос аргументов в
	/// локальные слоты вызываемого. Возвращает <c>Function = null</c>, если вызов нативный
	/// и уже выполнен (тогда цикл просто продолжается).
	/// </summary>
	private CallStart BeginCall(
		FunctionDef callee,
		int argc,
		FunctionDef caller,
		KValue[] callerLocals,
		KValue[]? callerCaptures,
		int callerPc,
		int callerStackBase,
		ref OperandStack stack,
		ref Frame[] frames,
		ref int frameCount)
	{
		if (argc != callee.ParamCount)
			throw new KernPanic(
				$"функция '{callee.Name}' ожидает {callee.ParamCount} аргументов, передано {argc}");
		int callBase = stack.Count - argc;
		if (callBase < callerStackBase)
			throw new KernPanic($"вызов '{callee.Name}': аргументы заходят ниже базы кадра");
		long profileStart = Profiler?.StartCall(callee) ?? 0;
		KValue[]? memoArguments = null;
		if (CanMemoize(callee))
		{
			memoArguments = new KValue[argc];
			stack.CopyTo(callBase, memoArguments, argc);
			var key = new MemoKey(callee, memoArguments);
			if (_memo.TryGetValue(key, out KValue cached))
			{
				stack.Truncate(callBase);
				stack.Push(MemoValue(cached));
				Profiler?.EndCall(callee, profileStart);
				return default;
			}
		}
		if (callee.IsNative)
		{
			InvokeNative(callee, argc, ref stack);
			Profiler?.EndCall(callee, profileStart);
			return default;
		}
		if (frameCount == frames.Length)
		{
			if (frames.Length >= MaxCallDepth)
				throw new KernPanic("переполнение стека вызовов");
			Array.Resize(ref frames, Math.Min(frames.Length * 2, MaxCallDepth));
		}
		if (frameCount >= MaxCallDepth)
			throw new KernPanic("переполнение стека вызовов");
		// Вызов конструктора неизменяемого unit открывает окно создания:
		// его пролог, цепочка и тело заполняют поля будущего значения.
		// Окно открывается только после проверок глубины и сразу
		// запоминается в кадре вызывающего: иначе отказ по глубине или
		// паника оставили бы объект записываемым навсегда.
		KObject? construction = null;
		if (argc > 0 &&
			IsImmutableUnitConstructor(callee) &&
			stack.At(callBase).Ref is KObject constructed)
		{
			constructed.BeginConstruction();
			construction = constructed;
		}
		frames[frameCount++] = new Frame(caller, callerLocals, callerCaptures, callerPc, callerStackBase, construction, memoArguments, profileStart);

		KValue[] calleeLocals = AllocateLocals(callee);
		stack.CopyTo(callBase, calleeLocals, argc);
		stack.Truncate(callBase);
		return new CallStart(callee, calleeLocals, callBase);
	}

	/// <summary>
	/// Результат <see cref="BeginCall"/>: новый кадр (<see cref="Function"/> = null —
	/// нативный вызов уже выполнен, кадр не создавался).
	/// </summary>
	private readonly struct CallStart(
		FunctionDef? function,
		KValue[]? locals,
		int stackBase)
	{
		public FunctionDef? Function { get; } = function;

		public KValue[]? Locals { get; } = locals;

		/// <summary>База стека вызываемого кадра (адрес слота получателя).</summary>
		public int StackBase { get; } = stackBase;
	}
}
