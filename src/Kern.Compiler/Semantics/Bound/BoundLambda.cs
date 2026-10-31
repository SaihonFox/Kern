using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>
/// Один захват замыкания: имя для диагностики, выражение-источник в кадре объемлющей
/// функции и тип захваченного значения. Источник — либо локальная переменная
/// (<see cref="BoundVariableExpression"/>), либо захват внешней лямбды
/// (<see cref="BoundCaptureExpression"/>), поэтому вложенные лямбды захватывают
/// транзитивно.
/// </summary>
public sealed record BoundCapture(string Name, BoundExpression Source, KernType Type);

/// <summary>
/// Чтение захваченного значения внутри лямбды: <c>LoadCapture index</c>.
/// Индекс — позиция в списке захватов <see cref="BoundLambdaExpression.Captures"/>
/// той лямбды, в теле которой стоит это чтение.
/// </summary>
public sealed record BoundCaptureExpression(
	int Index,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>
/// Лямбда как значение (<c>fun(i32 n) -&gt; i32 n * n</c>, spec §6): синтезированная
/// функция с телом и списком захватов, вычисляемых в кадре объемлющей функции.
/// Тип выражения — <see cref="FunctionType"/>.
/// </summary>
public sealed record BoundLambdaExpression(
	FunctionSymbol Symbol,
	BoundFunction Function,
	IReadOnlyList<BoundCapture> Captures,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);

/// <summary>
/// Вызов значения функционального типа (<c>f(1)</c>, <c>(fun(i32 n) -&gt; i32 n)(2)</c>):
/// кодогенерация печатает функцию-значение, аргументы и <c>CallValue</c>.
/// </summary>
public sealed record BoundValueCallExpression(
	BoundExpression Target,
	IReadOnlyList<BoundExpression> Arguments,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);
