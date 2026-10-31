using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record ListSpreadExpression(ExpressionNode Expression, TextSpan Span) : ExpressionNode(Span);
