using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>B4a2: or-паттерн <c>p1 | p2</c> — ветвь совпадает, если совпала любая альтернатива.</summary>
public sealed record OrPattern(IReadOnlyList<PatternNode> Alternatives, TextSpan Span) : PatternNode(Span);
