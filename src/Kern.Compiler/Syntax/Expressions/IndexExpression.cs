using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record IndexExpression(
	ExpressionNode Target,
	IReadOnlyList<ExpressionNode> Arguments,
	TextSpan Span) : ExpressionNode(Span);
