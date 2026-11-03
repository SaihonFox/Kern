using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>
/// Паттерн кортежа <c>(p1, p2)</c> (spec §3): субъект обязан быть кортежем той же арности.
/// Одиночный <c>(p)</c> — это группировка, а не кортеж из одного элемента.
/// </summary>
public sealed record TuplePattern(IReadOnlyList<PatternNode> Items, TextSpan Span) : PatternNode(Span);
