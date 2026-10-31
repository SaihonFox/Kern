using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record SelfExpression(TextSpan Span) : ExpressionNode(Span);
