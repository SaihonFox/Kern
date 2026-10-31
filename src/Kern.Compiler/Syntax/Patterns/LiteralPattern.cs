using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record LiteralPattern(string Text, TokenKind Kind, TextSpan Span) : PatternNode(Span);
