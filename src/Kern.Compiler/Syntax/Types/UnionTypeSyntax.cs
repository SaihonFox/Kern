using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>Union-тип в объявлении алиаса: <c>[X, Y]</c>.</summary>
public sealed record UnionTypeSyntax(
	IReadOnlyList<TypeSyntax> Alternatives,
	TextSpan Span) : TypeSyntax(Span)
{
	public override string ToString() => $"[{string.Join(',', Alternatives)}]";
}
