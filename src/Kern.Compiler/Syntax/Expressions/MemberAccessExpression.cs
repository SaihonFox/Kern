using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record MemberAccessExpression(
	ExpressionNode Target,
	string Member,
	bool IsConditional,
	TextSpan Span) : ExpressionNode(Span);
