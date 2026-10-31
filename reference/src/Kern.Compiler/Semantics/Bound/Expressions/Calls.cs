using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Bound.Expressions;

/// <summary>
/// Вызов функции/метода/расширения/аксессора с выбранной перегрузкой.
/// <para>
/// <see cref="Arguments"/> — РОВНО по одному выражению на каждый параметр <see cref="Function"/> в позиционном порядке:
/// переданное значение (уже обёрнуто в <see cref="BoundConversion"/>, если нужно), <see cref="BoundDefaultArgument"/>
/// для пропущенного параметра со значением по умолчанию, либо для variadic-параметра — <see cref="BoundList"/>
/// (упаковка `..args`) / готовый list&lt;T&gt; (нормальная форма). Именованные аргументы уже переставлены.
/// </para>
/// <para>
/// <see cref="EvaluationOrder"/> — индексы параметров, получивших аргументы, в порядке ВЫЧИСЛЕНИЯ (порядок аргументов в исходнике);
/// null — совпадает с позиционным. Нужен, чтобы `f(b: g(), a: h())` вычислял g раньше h; значения по умолчанию
/// вычисляются после всех явных аргументов.
/// </para>
/// Для расширения <see cref="Receiver"/> — расширяемый объект (передаётся первым скрытым аргументом).
/// <see cref="Type"/> — возвращаемый тип с подставленными <see cref="TypeArgs"/> и <see cref="OwnerTypeArgs"/>
/// (для `obj?.m()` — nullable).
/// </summary>
public sealed record BoundCall(
	FunctionSymbol Function,
	BoundExpr? Receiver,
	IReadOnlyList<TypeSymbol> TypeArgs,
	IReadOnlyList<TypeSymbol> OwnerTypeArgs,
	IReadOnlyList<BoundExpr> Arguments,
	IReadOnlyList<int>? EvaluationOrder,
	bool IsSafe,
	bool IsNonVirtual,
	TypeSymbol Type,
	TextSpan Span) : BoundExpr(Type, Span);

/// <summary>
/// Вызов значения-функции (лямбда, `fun`-переменная, результат выражения): `f(1, 2)`.
/// Именованные аргументы и значения по умолчанию для значений-функций не поддерживаются — аргументы позиционные.
/// </summary>
public sealed record BoundDelegateCall(BoundExpr Callee, IReadOnlyList<BoundExpr> Arguments, bool IsSafe, TypeSymbol Type, TextSpan Span)
	: BoundExpr(Type, Span);

/// <summary>
/// Создание объекта: `Point(1, 2)`, `T x(args)`. <see cref="Type"/> — конструируемый тип (с generic-аргументами).
/// <see cref="Constructor"/> == null — у типа нет объявленных конструкторов (инициализация по умолчанию).
/// Остальные поля — как в <see cref="BoundCall"/>.
/// </summary>
public sealed record BoundNew(
	FunctionSymbol? Constructor,
	IReadOnlyList<TypeSymbol> TypeArgs,
	IReadOnlyList<BoundExpr> Arguments,
	IReadOnlyList<int>? EvaluationOrder,
	TypeSymbol Type,
	TextSpan Span) : BoundExpr(Type, Span);

/// <summary>
/// Значение по умолчанию для пропущенного параметра. <see cref="Value"/> — привязанное выражение
/// <see cref="ParameterSymbol.Default"/> (вычисляется в контексте вызова, как в C#); null — нативное значение
/// по умолчанию (<see cref="ParameterSymbol.HasNativeDefault"/>), подставляет нативный вызов.
/// </summary>
public sealed record BoundDefaultArgument(ParameterSymbol Parameter, BoundExpr? Value, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>
/// Неявное преобразование <see cref="Operand"/> к <see cref="BoundNode.Type"/> (целевому типу).
/// <see cref="Kind"/> — вид из <see cref="Conversions.Classify"/>; Identity-преобразования не создаются.
/// <see cref="IsConstant"/> — константное приведение числового литерала (значение вписывается в целевой тип).
/// </summary>
public sealed record BoundConversion(BoundExpr Operand, ConversionKind Kind, bool IsConstant, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span)
{
	public TypeSymbol SourceType => Operand.Type;
}
