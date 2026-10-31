using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>
/// Литерал кортежа (spec §3): <c>(10, "hello")</c>. Элементы хранятся по порядку,
/// тип — <see cref="TupleType"/>. Имена элементов берутся из ожидаемого типа
/// (<c>(i32 x, i32 y) point = (3, 4)</c>), потому что отдельного именованного
/// синтаксиса литерала spec §3 не содержит.
/// </summary>
public sealed record BoundTupleExpression(
	IReadOnlyList<BoundExpression> Items,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>
/// Чтение элемента кортежа (spec §3): <c>t.x</c> и позиции деструктуризации
/// <c>(i32 a, str b) = t</c>. Имя элемента — метаданные типа, поэтому в узле
/// только индекс; проверку индекса делает VM (паника).
/// </summary>
public sealed record BoundTupleItemExpression(
	BoundExpression Target,
	int Index,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);
