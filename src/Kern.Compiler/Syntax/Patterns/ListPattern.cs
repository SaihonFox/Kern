using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>
/// B4b1: паттерн списка фиксированной длины <c>[p1, p2]</c> — субъект совпадает,
/// если его длина ровно N и каждый элемент проходит свой паттерн.
/// </summary>
public sealed record ListPattern(IReadOnlyList<PatternNode> Items, TextSpan Span) : PatternNode(Span);
