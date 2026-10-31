using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record WildcardPattern(TextSpan Span) : PatternNode(Span);
