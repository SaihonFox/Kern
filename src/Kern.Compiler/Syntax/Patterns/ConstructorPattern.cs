using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record ConstructorPattern(string Name, IReadOnlyList<PatternNode> Arguments, TextSpan Span) : PatternNode(Span);
