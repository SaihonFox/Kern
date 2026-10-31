using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record BinaryExpression(
	ExpressionNode Left,
	string Operator,
	ExpressionNode Right,
	TextSpan Span) : ExpressionNode(Span);
