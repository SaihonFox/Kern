using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record CallExpression(
	ExpressionNode Target,
	IReadOnlyList<ExpressionNode> Arguments,
	TextSpan Span,
	bool IsMacroInvocation = false) : ExpressionNode(Span);
