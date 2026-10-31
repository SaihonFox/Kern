using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record MatchExpression(
	ExpressionNode Target,
	IReadOnlyList<MatchArm> Arms,
	TextSpan Span,
	bool IsTargetless = false) : ExpressionNode(Span);
