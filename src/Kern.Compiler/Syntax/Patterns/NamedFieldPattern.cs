using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record NamedFieldPattern(string Name, PatternNode? Pattern, TextSpan Span) : SyntaxNode(Span);
