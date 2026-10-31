using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record IntegerLiteralExpression(string Text, TextSpan Span) : ExpressionNode(Span);
