using Kern.Bytecode;
using Kern.Vm.Async;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>
/// Интерпретатор байткода Kern. Разбит на partial-файлы:
/// Loop (диспетчер опкодов), Arith, Calls, Objects, Format, Async.
/// Потоковая модель: весь Kern-код исполняется в одном потоке (поток планировщика).
/// </summary>
public sealed partial class Interpreter
{
	[ThreadStatic] private static Interpreter? _current;

	/// <summary>VM, исполняющая код в текущем потоке (для ValueOps.ToStr, KTask.FromDotnet и нативов).</summary>
	public static Interpreter? Current { get => _current; internal set => _current = value; }

	private const int MaxFrameDepth = 20000;
	private const int MaxNativeDepth = 400;

	private readonly FunctionDef[] _funcs;
	private readonly bool[] _isInstance;
	private readonly KType?[] _sigCache;
	private readonly bool[] _sigDynamic;
	private bool _initialized;
	private int _nativeDepth;

	public KbcModule Module { get; }
	public INativeResolver Natives { get; }
	public KModuleInfo Info { get; }
	public TypeFactory Types => Info.Types;
	public Scheduler Scheduler { get; } = new();

	public Interpreter(KbcModule module, INativeResolver natives)
	{
		Module = module;
		Natives = natives;
		Info = ModuleLoader.Load(module);
		_funcs = [.. module.Functions];
		_isInstance = new bool[_funcs.Length];
		for (int i = 0; i < _funcs.Length; i++) _isInstance[i] = ComputeIsInstance(_funcs[i]);
		_sigCache = new KType?[module.TypeSigs.Count];
		_sigDynamic = new bool[module.TypeSigs.Count];
		for (int i = 0; i < _sigDynamic.Length; i++) _sigDynamic[i] = TypeFactory.HasParams(module.TypeSigs[i]);
	}

	/// <summary>
	/// Дочерний интерпретатор для другого потока: те же модуль, статические поля, классы и типы (общая память),
	/// но свои планировщик, фреймы и счётчики. Статическая инициализация выполняется один раз — в родителе.
	/// </summary>
	private Interpreter(Interpreter parent)
	{
		Module = parent.Module;
		Natives = parent.Natives;
		Info = parent.Info;
		_funcs = parent._funcs;
		_isInstance = parent._isInstance;
		_sigCache = parent._sigCache;
		_sigDynamic = parent._sigDynamic;
		_initialized = true;
	}

	/// <summary>Создать интерпретатор для рабочего потока, разделяющий с этим данные программы. Вызывать из потока-владельца.</summary>
	public Interpreter Fork()
	{
		Initialize();
		return new Interpreter(this);
	}

	/// <summary>Привязать CLR-тип к имени Kern-типа вне модуля (для IsType/typeof).</summary>
	public void RegisterNativeType(string kernName, Type clr) => Types.RegisterNative(kernName, clr);

	private static bool ComputeIsInstance(FunctionDef f)
	{
		var fl = f.Flags;
		if (fl.HasFlag(MemberFlags.Extension)) return true;
		if (fl.HasFlag(MemberFlags.Static) || fl.HasFlag(MemberFlags.Lambda)) return false;
		return f.Owner >= 0;
	}

	public FunctionDef GetFunction(int index) => _funcs[index];

	/// <summary>Выполнить инициализаторы статических полей (один раз).</summary>
	public void Initialize()
	{
		if (_initialized) return;
		_initialized = true;
		foreach (int fi in Module.StaticInit) CallFunctionIndex(fi, []);
	}

	// ---------- типы ----------

	/// <summary>Разрешить подпись из пула модуля с учётом параметров типа фрейма.</summary>
	internal KType ResolveSig(int sigIdx, KType[] classArgs, KType[] methodArgs)
	{
		if (!_sigDynamic[sigIdx])
			return _sigCache[sigIdx] ??= Types.Resolve(Module.TypeSigs[sigIdx], [], []);
		return Types.Resolve(Module.TypeSigs[sigIdx], classArgs, methodArgs);
	}

	/// <summary>Разрешить список подписей (аргументы типа вызова).</summary>
	internal KType[] ResolveList(int listIdx, KType[] classArgs, KType[] methodArgs)
	{
		if (listIdx < 0) return [];
		var ids = Module.TypeSigLists[listIdx];
		var r = new KType[ids.Length];
		for (int i = 0; i < r.Length; i++) r[i] = ResolveSig(ids[i], classArgs, methodArgs);
		return r;
	}

	public KType TypeOf(KValue v) => TypeChecks.TypeOf(v, Types);
	public bool IsInstance(KValue v, KType t) => TypeChecks.IsInstance(v, t);

	// ---------- паники ----------

	private void FillTrace(KernPanic p, Frame? top, int topPc)
	{
		int pc = topPc;
		for (var f = top; f is not null; f = f.Caller)
		{
			p.Trace.Add(new KernStackFrame(FrameName(f.Def), f.Def.SourceFile, f.LineAt(pc)));
			pc = f.Caller?.Pc ?? 0;
		}
	}

	private string FrameName(FunctionDef d) =>
		d.Owner >= 0 && d.Owner < Module.Types.Count ? Module.Types[d.Owner].Name + "." + d.Name : d.Name;

	/// <summary>Превратить произвольное исключение CLR в panic Kern.</summary>
	internal static KernPanic ToPanic(Exception e) => e switch
	{
		KernPanic p => p,
		DivideByZeroException => new KernPanic("деление на ноль", e),
		InvalidCastException => new KernPanic("неверное приведение типа: " + e.Message, e),
		IndexOutOfRangeException or ArgumentOutOfRangeException => new KernPanic("индекс вне диапазона", e),
		KeyNotFoundException => new KernPanic("ключ не найден", e),
		NullReferenceException => new KernPanic("обращение к nil", e),
		StackOverflowException or InsufficientExecutionStackException => new KernPanic("переполнение стека", e),
		_ => new KernPanic(e.Message, e),
	};
}
