namespace Kern.Compiler.Semantics.Types;

/// <summary>Unit-тип; <c>unit!</c> отличается флагом <see cref="IsImmutable"/>.</summary>
public sealed record UnitType : KernType
{
	public UnitType(TypeSymbol symbol, IReadOnlyList<KernType> arguments, bool isImmutable) : base(TypeKind.Unit)
	{
		Symbol = symbol;
		Arguments = StructuralList<KernType>.From(arguments);
		IsImmutable = isImmutable;
	}

	public TypeSymbol Symbol { get; }

	public IReadOnlyList<KernType> Arguments { get; }

	public bool IsImmutable { get; }

	public override string ToString() => TypeText.WithArguments(Symbol.Name, Arguments);
}
