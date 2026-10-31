using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record NumberLiteralExpression(string Text, TextSpan Span) : ExpressionNode(Span);
