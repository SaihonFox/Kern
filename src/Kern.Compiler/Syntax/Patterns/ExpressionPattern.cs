using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record ExpressionPattern(ExpressionNode Expression, TextSpan Span) : PatternNode(Span);
