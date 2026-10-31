using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record RangeExpression(
	ExpressionNode Start,
	ExpressionNode End,
	bool IsInclusive,
	ExpressionNode? Step,
	TextSpan Span) : ExpressionNode(Span);
