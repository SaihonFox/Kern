using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record CallArgumentNode(string? Name, ExpressionNode Value, TextSpan Span);

public sealed record StructuredCallExpression(
	ExpressionNode Target,
	IReadOnlyList<TypeSyntax> TypeArguments,
	IReadOnlyList<CallArgumentNode> Arguments,
	TextSpan Span) : ExpressionNode(Span);
