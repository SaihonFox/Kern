namespace Kern.Compiler.Semantics.Types;

/// <summary>Элемент кортежа: тип и необязательное имя поля.</summary>
public sealed record TupleElement(KernType Type, string? Name)
{
	public override string ToString() => Name is null ? Type.ToString() : $"{Type} {Name}";
}

/// <summary>Кортеж <c>(i32 x, f64 y)</c>.</summary>
public sealed record TupleType : KernType
{
	public TupleType(IReadOnlyList<TupleElement> elements) : base(TypeKind.Tuple) =>
		Elements = StructuralList<TupleElement>.From(elements);

	public IReadOnlyList<TupleElement> Elements { get; }

	public override string ToString() => $"({string.Join(",", Elements)})";
}
