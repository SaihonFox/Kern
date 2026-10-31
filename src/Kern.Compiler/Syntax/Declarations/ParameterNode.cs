using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record ParameterNode(
	string Name,
	TextSpan Span,
	TypeSyntax? Type = null,
	ExpressionNode? DefaultValue = null,
	bool IsAutoInit = false) : SyntaxNode(Span);
