using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>Кусок интерполированной строки: литеральный текст или выражение (spec §3).</summary>
public abstract record InterpolationPartNode(TextSpan Span) : SyntaxNode(Span);

/// <summary>Литеральный текст между <c>${…}</c>; escape-последовательности уже сняты.</summary>
public sealed record InterpolationTextPart(string Text, TextSpan Span) : InterpolationPartNode(Span);

/// <summary>
/// Выражение внутри <c>${…}</c>. <paramref name="Spec"/> = null, если формат не задан;
/// иначе — текст после первого двоеточия верхнего уровня (не разбирается как Kern).
/// <paramref name="Debug"/> включает вывод <c>expr=value</c>, <paramref name="DebugText"/> хранит
/// исходный текст выражения до <c>=</c>. <see cref="Expression"/> = null для пустого <c>${}</c>.
/// </summary>
public sealed record InterpolationExpressionPart(
	ExpressionNode? Expression,
	string? Spec,
	bool Debug,
	string? DebugText,
	TextSpan Span) : InterpolationPartNode(Span);

/// <summary>Интерполированная строка <c>f"…"</c>/<c>fr"…"</c>/<c>fv"…"</c> (spec §3).</summary>
public sealed record InterpolatedStringExpression(
	IReadOnlyList<InterpolationPartNode> Parts,
	TextSpan Span) : ExpressionNode(Span);
