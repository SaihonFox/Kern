using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record TypePattern(TypeSyntax Type, string Name, TextSpan Span) : PatternNode(Span);
