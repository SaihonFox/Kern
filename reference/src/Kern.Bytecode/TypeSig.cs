namespace Kern.Bytecode;

public enum PrimKind : byte
{
	Void, Bool, Char, Str, Object, Nil,
	I8, I16, I32, I64, U8, U16, U32, U64, F16, F32, F64,
	/// <summary>Универсальные числовые ограничения (только как констрейнты/выводы).</summary>
	INum, FNum, Num,
	/// <summary>Значение typeof(...).</summary>
	Type,
}

/// <summary>Подпись типа в пуле модуля. Иммутабельное дерево; сравнивается структурно.</summary>
public abstract record TypeSig
{
	public sealed record Prim(PrimKind Kind) : TypeSig;

	/// <summary>Тип из этого модуля (индекс в Types) или внешний/нативный (полное имя), с аргументами.</summary>
	public sealed record Named(string FullName, IReadOnlyList<TypeSig> Args) : TypeSig
	{
		public bool Equals(Named? o) => o is not null && FullName == o.FullName && Args.SequenceEqual(o.Args);
		public override int GetHashCode() => HashCode.Combine(FullName, Args.Count);
	}

	/// <summary>Параметр типа. Scope: 0 = параметр класса, 1 = параметр метода.</summary>
	public sealed record Param(int Scope, int Index, string Name) : TypeSig;

	public sealed record Func(IReadOnlyList<TypeSig> Params, TypeSig Return) : TypeSig
	{
		public bool Equals(Func? o) => o is not null && Return == o.Return && Params.SequenceEqual(o.Params);
		public override int GetHashCode() => HashCode.Combine(Return, Params.Count);
	}

	public sealed record Tuple(IReadOnlyList<TypeSig> Items, IReadOnlyList<string?>? Names) : TypeSig
	{
		public bool Equals(Tuple? o) => o is not null && Items.SequenceEqual(o.Items);
		public override int GetHashCode() => HashCode.Combine(Items.Count);
	}

	public sealed record Nullable(TypeSig Inner) : TypeSig;

	public sealed record Union(IReadOnlyList<TypeSig> Members) : TypeSig
	{
		public bool Equals(Union? o) => o is not null && Members.SequenceEqual(o.Members);
		public override int GetHashCode() => HashCode.Combine(Members.Count);
	}

	public static readonly TypeSig Void = new Prim(PrimKind.Void);
	public static readonly TypeSig Bool = new Prim(PrimKind.Bool);
	public static readonly TypeSig Str = new Prim(PrimKind.Str);
	public static readonly TypeSig I32 = new Prim(PrimKind.I32);
	public static readonly TypeSig I64 = new Prim(PrimKind.I64);
	public static readonly TypeSig F64 = new Prim(PrimKind.F64);
	public static readonly TypeSig Object = new Prim(PrimKind.Object);
}
