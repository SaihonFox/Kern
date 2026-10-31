using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed record RangePattern(PatternNode Start, PatternNode End, bool IsInclusive, TextSpan Span) : PatternNode(Span);
