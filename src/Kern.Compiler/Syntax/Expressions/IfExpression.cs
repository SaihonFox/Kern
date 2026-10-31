using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record IfExpression(
	ExpressionNode Condition,
	SyntaxNode Then,
	SyntaxNode? Else,
	TextSpan Span) : ExpressionNode(Span);
