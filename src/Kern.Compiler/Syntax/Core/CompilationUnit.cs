using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record CompilationUnit(IReadOnlyList<DeclarationNode> Declarations, TextSpan Span) : SyntaxNode(Span)
{
	public string? Project { get; init; }
}
