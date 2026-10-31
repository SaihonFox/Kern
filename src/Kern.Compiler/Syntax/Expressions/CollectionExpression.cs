using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record CollectionExpression(
	IReadOnlyList<ExpressionNode> Items,
	TextSpan Span) : ExpressionNode(Span);
