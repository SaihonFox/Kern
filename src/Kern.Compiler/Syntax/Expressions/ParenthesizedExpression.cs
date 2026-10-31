using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record ParenthesizedExpression(ExpressionNode Expression, TextSpan Span) : ExpressionNode(Span);
