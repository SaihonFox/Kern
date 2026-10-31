using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record TupleExpression(IReadOnlyList<ExpressionNode> Items, TextSpan Span) : ExpressionNode(Span);
