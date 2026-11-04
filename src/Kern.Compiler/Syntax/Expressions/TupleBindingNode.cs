using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record TupleBindingNode(
	TypeSyntax? Type,
	string? Name,
	bool IsIgnored,
	TextSpan Span,
	string? RestName = null) : SyntaxNode(Span)
{
	/// <summary>Вложенный шаблон кортежа: <c>(i32 a, (i32 b, i32 c)) = pair</c>.</summary>
	public IReadOnlyList<TupleBindingNode>? Nested { get; init; }
}
