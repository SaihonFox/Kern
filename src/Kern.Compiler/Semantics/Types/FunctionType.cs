namespace Kern.Compiler.Semantics.Types;

/// <summary>Функциональный тип <c>fun(i32, str) -&gt; bool</c>; отсутствие возврата — <c>void</c>.</summary>
public sealed record FunctionType : KernType
{
	public FunctionType(IReadOnlyList<KernType> parameters, KernType returnType) : base(TypeKind.Function)
	{
		Parameters = StructuralList<KernType>.From(parameters);
		ReturnType = returnType;
	}

	public IReadOnlyList<KernType> Parameters { get; }

	public KernType ReturnType { get; }

	public override string ToString() => $"fun({string.Join(",", Parameters)})->{ReturnType}";
}
