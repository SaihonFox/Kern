namespace Kern.Compiler.Semantics.Types;

/// <summary>Пользовательский тип (<c>type Point&lt;T&gt;</c>).</summary>
public sealed record ClassType : KernType
{
	public ClassType(TypeSymbol symbol, IReadOnlyList<KernType> arguments) : base(TypeKind.Class)
	{
		Symbol = symbol;
		Arguments = StructuralList<KernType>.From(arguments);
	}

	public TypeSymbol Symbol { get; }

	public IReadOnlyList<KernType> Arguments { get; }

	public override string ToString() => TypeText.WithArguments(Symbol.Name, Arguments);
}
