namespace Kern.Bytecode;

/// <summary>Базовый (не составной) тип в подписи.</summary>
public enum PrimKind : byte
{
	Void = 0,
	Bool,
	Char,
	Str,
	Object,
	Nil,
	None,
	I8,
	I16,
	I32,
	I64,
	U8,
	U16,
	U32,
	U64,
	F16,
	F32,
	F64,
	/// <summary>Ограничение «любое целое» (только в constraints/выводе).</summary>
	INum,
	/// <summary>Ограничение «любое дробное».</summary>
	FNum,
	/// <summary>Ограничение «любое число».</summary>
	Num,
	/// <summary>Значение <c>typeof(...)</c>.</summary>
	Type,
	/// <summary>Тип-дно (panic, ret, stop, next).</summary>
	Never,
}

/// <summary>Kern-имена базовых типов; используются в каноническом тексте подписей.</summary>
public static class PrimNames
{
	public static bool IsDefined(PrimKind kind) => (byte)kind <= (byte)PrimKind.Never;

	public static string ToKernName(this PrimKind kind) => kind switch
	{
		PrimKind.Void => "void",
		PrimKind.Bool => "bool",
		PrimKind.Char => "char",
		PrimKind.Str => "str",
		PrimKind.Object => "object",
		PrimKind.Nil => "nil",
		PrimKind.None => "none",
		PrimKind.I8 => "i8",
		PrimKind.I16 => "i16",
		PrimKind.I32 => "i32",
		PrimKind.I64 => "i64",
		PrimKind.U8 => "u8",
		PrimKind.U16 => "u16",
		PrimKind.U32 => "u32",
		PrimKind.U64 => "u64",
		PrimKind.F16 => "f16",
		PrimKind.F32 => "f32",
		PrimKind.F64 => "f64",
		PrimKind.INum => "inum",
		PrimKind.FNum => "fnum",
		PrimKind.Num => "num",
		PrimKind.Type => "type",
		PrimKind.Never => "never",
		_ => "?",
	};
}

/// <summary>
/// Подпись типа в пуле модуля: иммутабельное дерево, сравнивается структурно.
/// <see cref="Canonical"/> — плоский канонический текст (ключ для интернирования и .kbc);
/// структура и текст обязаны совпадать, это проверяет <see cref="KbcReader"/>.
/// </summary>
public abstract record TypeSig
{
	public abstract string Canonical { get; }

	public static TypeSig Prim(PrimKind kind) => new PrimSig(kind);

	public static readonly TypeSig Void = new PrimSig(PrimKind.Void);
	public static readonly TypeSig Bool = new PrimSig(PrimKind.Bool);
	public static readonly TypeSig Char = new PrimSig(PrimKind.Char);
	public static readonly TypeSig Str = new PrimSig(PrimKind.Str);
	public static readonly TypeSig Object = new PrimSig(PrimKind.Object);
	public static readonly TypeSig Nil = new PrimSig(PrimKind.Nil);
	public static readonly TypeSig None = new PrimSig(PrimKind.None);
	public static readonly TypeSig I8 = new PrimSig(PrimKind.I8);
	public static readonly TypeSig I16 = new PrimSig(PrimKind.I16);
	public static readonly TypeSig I32 = new PrimSig(PrimKind.I32);
	public static readonly TypeSig I64 = new PrimSig(PrimKind.I64);
	public static readonly TypeSig U8 = new PrimSig(PrimKind.U8);
	public static readonly TypeSig U16 = new PrimSig(PrimKind.U16);
	public static readonly TypeSig U32 = new PrimSig(PrimKind.U32);
	public static readonly TypeSig U64 = new PrimSig(PrimKind.U64);
	public static readonly TypeSig F16 = new PrimSig(PrimKind.F16);
	public static readonly TypeSig F32 = new PrimSig(PrimKind.F32);
	public static readonly TypeSig F64 = new PrimSig(PrimKind.F64);
	public static readonly TypeSig Never = new PrimSig(PrimKind.Never);

	/// <summary>Базовый тип.</summary>
	public sealed record PrimSig(PrimKind Kind) : TypeSig
	{
		public override string Canonical => Kind.ToKernName();
	}

	/// <summary>Именованный тип (из TypePool или внешний) с аргументами дженерика.</summary>
	public sealed record NamedSig(string FullName, TypeSig[] Args) : TypeSig
	{
		public override string Canonical =>
			Args.Length == 0 ? FullName : $"{FullName}<{string.Join(", ", TypeSigText.Join(Args))}>";

		public bool Equals(NamedSig? other) =>
			other is not null && FullName == other.FullName && TypeSigText.SequenceEqual(Args, other.Args);

		public override int GetHashCode() => HashCode.Combine(FullName, Args.Length, TypeSigText.Hash(Args));
	}

	/// <summary>Параметр типа. Scope: 0 — тип, 1 — функция. В каноническом тексте — <c>$scope:name</c>.</summary>
	public sealed record ParamSig(int Scope, int Index, string Name) : TypeSig
	{
		public override string Canonical => $"${Scope}:{(Name.Length == 0 ? Index.ToString() : Name)}";
	}

	/// <summary>Функциональный тип.</summary>
	public sealed record FuncSig(TypeSig[] Params, TypeSig Return) : TypeSig
	{
		public override string Canonical =>
			$"fun({string.Join(", ", TypeSigText.Join(Params))}) -> {Return.Canonical}";

		public bool Equals(FuncSig? other) =>
			other is not null && Return.Equals(other.Return) && TypeSigText.SequenceEqual(Params, other.Params);

		public override int GetHashCode() => HashCode.Combine(Return, Params.Length, TypeSigText.Hash(Params));
	}

	/// <summary>Кортеж. Имена элементов — метаданные и в идентичность/канонический текст не входят.</summary>
	public sealed record TupleSig(TypeSig[] Items, string?[]? Names) : TypeSig
	{
		public override string Canonical => $"({string.Join(", ", TypeSigText.Join(Items))})";

		public bool Equals(TupleSig? other) =>
			other is not null && TypeSigText.SequenceEqual(Items, other.Items);

		public override int GetHashCode() => HashCode.Combine(Items.Length, TypeSigText.Hash(Items));
	}

	/// <summary>Nullable-тип <c>T?</c>.</summary>
	public sealed record NullableSig(TypeSig Inner) : TypeSig
	{
		public override string Canonical => $"{Inner.Canonical}?";
	}

	/// <summary>Union из unit-типов: <c>[A, B]</c>.</summary>
	public sealed record UnionSig(TypeSig[] Members) : TypeSig
	{
		public override string Canonical => $"[{string.Join(", ", TypeSigText.Join(Members))}]";

		public bool Equals(UnionSig? other) =>
			other is not null && TypeSigText.SequenceEqual(Members, other.Members);

		public override int GetHashCode() => HashCode.Combine(Members.Length, TypeSigText.Hash(Members));
	}
}

/// <summary>Служебные операции над подписями: структурное сравнение и канонический текст.</summary>
internal static class TypeSigText
{
	public static bool SequenceEqual(TypeSig[] a, TypeSig[] b)
	{
		if (ReferenceEquals(a, b)) return true;
		if (a.Length != b.Length) return false;
		for (int i = 0; i < a.Length; i++)
			if (!a[i].Equals(b[i])) return false;
		return true;
	}

	public static int Hash(TypeSig[] a)
	{
		HashCode hash = default;
		foreach (TypeSig s in a) hash.Add(s);
		return hash.ToHashCode();
	}

	public static IEnumerable<string> Join(TypeSig[] sigs) => sigs.Select(s => s.Canonical);
}
