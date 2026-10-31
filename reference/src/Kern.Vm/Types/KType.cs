using Kern.Bytecode;

namespace Kern.Vm.Types;

/// <summary>Reified-тип во время выполнения. Интернируется: ссылочное равенство = равенство типов.</summary>
public abstract class KType
{
	private static int _nextId;
	/// <summary>Уникальный номер типа (ключи интернирования; в отличие от хэш-кода не коллизирует).</summary>
	public int Id { get; } = Interlocked.Increment(ref _nextId);
	public abstract string Name { get; }
	public override string ToString() => Name;
}

public sealed class KPrimType(PrimKind kind) : KType
{
	public PrimKind Kind { get; } = kind;
	public override string Name => Kind.ToString().ToLowerInvariant();
}

public sealed class KClassType(TypeDef def, int defIndex, KType[] args, KModuleInfo module) : KType
{
	public TypeDef Def { get; } = def;
	public int DefIndex { get; } = defIndex;
	public KType[] Args { get; } = args;
	public KModuleInfo Module { get; } = module;
	public override string Name => Args.Length == 0 ? Def.FullName : $"{Def.FullName}<{string.Join(", ", Args.Select(a => a.Name))}>";
}

public sealed class KFuncType(KType[] parameters, KType ret) : KType
{
	public KType[] Params { get; } = parameters;
	public KType Return { get; } = ret;
	public override string Name => $"fun({string.Join(", ", Params.Select(p => p.Name))}) -> {Return.Name}";
}

public sealed class KTupleType(KType[] items, string?[]? names) : KType
{
	public KType[] Items { get; } = items;
	public string?[]? Names { get; } = names;
	public override string Name => $"({string.Join(", ", Items.Select(i => i.Name))})";
}

public sealed class KNullableType(KType inner) : KType
{
	public KType Inner { get; } = inner;
	public override string Name => Inner.Name + "?";
}

public sealed class KUnionType(KType[] members) : KType
{
	public KType[] Members { get; } = members;
	public override string Name => "[" + string.Join(", ", Members.Select(m => m.Name)) + "]";
}

/// <summary>Встроенные обобщённые контейнеры: list/array/dict/result/task/... Name — имя без аргументов.</summary>
public sealed class KBuiltinGenericType(string name, KType[] args) : KType
{
	public string Builtin { get; } = name;
	public KType[] Args { get; } = args;
	public override string Name => $"{Builtin}<{string.Join(", ", Args.Select(a => a.Name))}>";
}

/// <summary>Нативный (C#) тип, привязанный через [KernType] или `use dotnet`.</summary>
public sealed class KNativeType(Type clrType, string kernName, KType[] args) : KType
{
	public Type ClrType { get; } = clrType;
	public KType[] Args { get; } = args;
	public override string Name => Args.Length == 0 ? kernName : $"{kernName}<{string.Join(", ", Args.Select(a => a.Name))}>";
}
