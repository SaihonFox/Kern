using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Bound.Expressions;

/// <summary>
/// Унарная операция. <see cref="Operator"/> — выбранный `op`-символ (пользовательский/нативный/расширение);
/// null — встроенная операция над примитивом (`!bool`, `-i32`).
/// </summary>
public sealed record BoundUnary(Syn.UnaryOp Op, FunctionSymbol? Operator, BoundExpr Operand, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>
/// Бинарная операция. <see cref="Operator"/> — выбранный символ; null — встроенная (арифметика/сравнение примитивов,
/// `&amp;&amp;`/`||` с короткой схемой, `===`/`!==` по ссылке, `==` по умолчанию).
/// Для <c>In</c>/<c>NotIn</c> получатель оператора — <see cref="Right"/> (`a in b` ≡ `b.op in(a)`).
/// <c>Elvis</c> представлен отдельным узлом <see cref="BoundCoalesce"/>.
/// </summary>
public sealed record BoundBinary(Syn.BinaryOp Op, FunctionSymbol? Operator, BoundExpr Left, BoundExpr Right, TypeSymbol Type, TextSpan Span)
	: BoundExpr(Type, Span);

/// <summary>`left ?: right` — <see cref="Right"/>, если левый операнд равен nil или none. Type — общий тип.</summary>
public sealed record BoundCoalesce(BoundExpr Left, BoundExpr Right, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>
/// `target = value`. Target — <see cref="BoundLocal"/>, <see cref="BoundParameter"/>, <see cref="BoundField"/>,
/// <see cref="BoundProperty"/>, <see cref="BoundIndex"/> (с <see cref="BoundIndex.Setter"/>). Для `obj?.p = v` — IsSafe у цели.
/// Значение уже приведено к типу цели. Type — тип цели (присваивание — выражение).
/// </summary>
public sealed record BoundAssignment(BoundExpr Target, BoundExpr Value, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>
/// `target op= value`. <see cref="Operator"/> — выбранный символ бинарной операции (null — встроенная).
/// Цель вычисляется один раз. Результат операции приводится к типу цели (<see cref="ResultConversion"/> — Identity, если не нужно).
/// </summary>
public sealed record BoundCompoundAssignment(
	Syn.AssignOp Op, FunctionSymbol? Operator, BoundExpr Target, BoundExpr Value, ConversionKind ResultConversion, TypeSymbol Type, TextSpan Span)
	: BoundExpr(Type, Span);

/// <summary>`x is T` / `x !is T`. Сужение типа выполняет биндер (flow-анализ), в дереве остаётся проверка.</summary>
public sealed record BoundTypeTest(BoundExpr Operand, TypeSymbol TestType, bool Negated, TextSpan Span) : BoundExpr(PrimitiveType.Bool, Span);
