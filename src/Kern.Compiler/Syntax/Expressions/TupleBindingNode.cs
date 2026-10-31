using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record TupleBindingNode(
	TypeSyntax? Type,
	string? Name,
	bool IsIgnored,
	TextSpan Span,
	string? RestName = null) : SyntaxNode(Span);
