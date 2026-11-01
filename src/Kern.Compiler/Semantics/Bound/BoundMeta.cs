using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>
/// <c>typeof&lt;T&gt;()</c> (M3, spec §17/§18): реифицированный тип. Codegen кладёт в
/// байткод <c>TypeOfSig</c> с подписью типа, VM поднимает её в значение <c>type</c>.
/// Статический результат — <c>object</c> (носитель значения <c>type</c>).
/// </summary>
public sealed record BoundTypeOfExpression(
	KernType TypeArgument,
	TextSpan Span) : BoundExpression(PrimitiveType.Object, Span);

/// <summary>
/// <c>typeof(x)</c> (spec round 18): тип значения; codegen печатает значение и
/// <c>TypeOfValue</c>. Статический результат — <c>object</c> (значение <c>type</c>).
/// </summary>
public sealed record BoundTypeOfValueExpression(
	BoundExpression Value,
	TextSpan Span) : BoundExpression(PrimitiveType.Object, Span);

/// <summary>
/// <c>name_of&lt;T&gt;()</c> (spec §18, рефлексия во время компиляции): биндер вычисляет
/// канонический текст типа, в байткоде остаётся строковый литерал. Форма значения
/// <c>name_of(x)</c> в этот слайс не входит.
/// </summary>
public sealed record BoundNameOfExpression(
	KernType TypeArgument,
	TextSpan Span) : BoundExpression(PrimitiveType.Str, Span);
