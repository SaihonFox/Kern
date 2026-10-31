using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Reflection;

/// <summary>Функция/метод/конструктор модуля. Перегрузки — отдельные объекты (по одному на FunctionDef).</summary>
public sealed class MethodInfo
{
	private readonly Interpreter _vm;
	private IReadOnlyList<ParameterInfo>? _params;
	private IReadOnlyList<AttributeInfo>? _attrs;

	internal MethodInfo(Interpreter vm, int index, TypeInfo? declaring)
	{
		_vm = vm;
		Index = index;
		Def = vm.GetFunction(index);
		Declaring = declaring;
	}

	public int Index { get; }
	public FunctionDef Def { get; }
	public TypeInfo? Declaring { get; }
	public string Name => Def.Name;
	public MemberAccess Access => Reflector.AccessOf(Def.Flags, Declaring is null ? MemberAccess.Public : MemberAccess.Private);
	public bool IsStatic => Def.Owner < 0 || Def.Flags.HasFlag(MemberFlags.Static);
	public bool IsAsync => Def.Flags.HasFlag(MemberFlags.Async);
	public bool IsConstructor => Def.Flags.HasFlag(MemberFlags.Ctor);
	public bool IsOperator => Def.Flags.HasFlag(MemberFlags.Operator);
	public bool IsExtension => Def.Flags.HasFlag(MemberFlags.Extension);
	public bool IsAbstract => Def.Flags.HasFlag(MemberFlags.Abstract);
	public bool IsGenericDefinition => Def.GenericParams.Count > 0;

	private KType[] ClassArgs => (Declaring?.Type as KClassType)?.Args ?? [];
	private KType[] MethodArgs => [.. Def.GenericParams.Select(_ => (KType)TypeFactory.Object)];

	public IReadOnlyList<ParameterInfo> Parameters => _params ??=
		[.. Def.Params.Select((p, i) => new ParameterInfo(p.Name, _vm.Types.Resolve(p.Type, ClassArgs, MethodArgs), p, i))];

	public KType ReturnType => _vm.Types.Resolve(Def.Return, ClassArgs, MethodArgs);

	public IReadOnlyList<AttributeInfo> Attributes => _attrs ??= AttributeInfo.Wrap(_vm, Def.Attributes);
	public bool HasAttribute(string name) => Def.Attributes.Any(a => Reflector.NameMatches(a.TypeName, name));
	public AttributeInfo? GetAttribute(string name) => Attributes.FirstOrDefault(a => a.Is(name));

	/// <summary>Пригодна ли функция для вызова с такими аргументами (число и типы).</summary>
	public bool Accepts(ReadOnlySpan<KValue> args)
	{
		var ps = Parameters;
		if (args.Length > ps.Count && !(ps.Count > 0 && ps[^1].IsVariadic)) return false;
		for (int i = 0; i < ps.Count; i++)
		{
			if (i >= args.Length) { if (!ps[i].HasDefault && !ps[i].IsVariadic) return false; continue; }
			if (ps[i].IsVariadic) break;
			if (Reflector.ParamScore(args[i], ps[i].Type) < 0) return false;
		}
		return true;
	}

	/// <summary>Вызвать. Для статических/верхнеуровневых функций <paramref name="target"/> игнорируется (передайте default).</summary>
	public KValue Invoke(KValue target, params KValue[] args)
	{
		var coerced = new KValue[args.Length];
		for (int i = 0; i < args.Length; i++)
			coerced[i] = i < Parameters.Count ? Reflector.Coerce(args[i], Parameters[i].Type) : args[i];

		if (IsStatic) return _vm.CallFunctionIndex(Index, coerced);
		if (target.Ref is KObject o) return _vm.CallMethod(o, Index, coerced);
		throw new KernPanic($"метод {Name} требует объект-получатель");
	}

	public override string ToString() =>
		$"{(Declaring is null ? "" : Declaring.Name + ".")}{Name}({string.Join(", ", Parameters)}) -> {ReturnType.Name}";
}
