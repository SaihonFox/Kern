using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record UnaryExpression(
	string Operator,
	ExpressionNode Operand,
	TextSpan Span) : ExpressionNode(Span);
