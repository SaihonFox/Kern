using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record GenericCallExpression(
	ExpressionNode Target,
	IReadOnlyList<TypeSyntax> TypeArguments,
	IReadOnlyList<ExpressionNode> Arguments,
	TextSpan Span) : ExpressionNode(Span);
