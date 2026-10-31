using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record BlockExpression(BlockStatement Block, TextSpan Span) : ExpressionNode(Span);
