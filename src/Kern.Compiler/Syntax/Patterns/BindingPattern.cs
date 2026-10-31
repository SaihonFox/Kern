using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record BindingPattern(string Name, bool IsMutable, TextSpan Span) : PatternNode(Span);
