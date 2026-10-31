using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Bound.Expressions;

/// <summary>Чтение локальной переменной. Type — тип с учётом сужения flow-анализом (`T?` → `T`).</summary>
public sealed record BoundLocal(LocalSymbol Local, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>Чтение параметра функции/лямбды/конструктора. Для variadic тип — list&lt;T&gt; (<see cref="ParameterSymbol.EffectiveType"/>).</summary>
public sealed record BoundParameter(ParameterSymbol Parameter, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>`$` — текущий объект (или левый операнд в операторных расширениях и расширениях типов).</summary>
public sealed record BoundThis(TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>
/// `$[A].member` — доступ/вызов через конкретного родителя: статическая диспетчеризация без виртуальности.
/// Используется как <see cref="BoundCall.Receiver"/> вместе с <see cref="BoundCall.IsNonVirtual"/>.
/// </summary>
public sealed record BoundParentThis(TypeSymbol Parent, TextSpan Span) : BoundExpr(Parent, Span);

/// <summary>
/// Доступ к полю (в т.ч. глобальной переменной, элементу enum, backing-полю).
/// <see cref="Receiver"/> == null — статическое/глобальное/enum. <see cref="IsSafe"/> — `obj?.field` (тип результата — nullable).
/// </summary>
public sealed record BoundField(BoundExpr? Receiver, FieldSymbol Field, bool IsSafe, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>
/// Доступ к свойству (чтение или цель присваивания; нативные свойства — через <see cref="PropertySymbol.Native"/>).
/// Для расширений свойств <see cref="Receiver"/> — расширяемый объект (`$`).
/// </summary>
public sealed record BoundProperty(BoundExpr? Receiver, PropertySymbol Property, bool IsSafe, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>
/// `target[i, j]` через оператор `[]`. <see cref="Getter"/> — выбранная перегрузка чтения (индексы), <see cref="Setter"/> —
/// перегрузка записи (индексы + значение), выбирается биндером при использовании как цели присваивания.
/// </summary>
public sealed record BoundIndex(
	BoundExpr Target, IReadOnlyList<BoundExpr> Indices, FunctionSymbol? Getter, FunctionSymbol? Setter, bool IsSafe, TypeSymbol Type, TextSpan Span)
	: BoundExpr(Type, Span);

/// <summary>Элемент кортежа `t.x` / `t.0`.</summary>
public sealed record BoundTupleItem(BoundExpr Target, int Index, string? Name, bool IsSafe, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>
/// Функция как значение (ссылка на метод `obj.method`, на функцию или конструктор). Type — <see cref="FuncType"/>.
/// <see cref="Receiver"/> захватывается (bound delegate); null — статическая функция.
/// </summary>
public sealed record BoundFunctionRef(
	FunctionSymbol Function, BoundExpr? Receiver, IReadOnlyList<TypeSymbol> TypeArgs, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>`expr!` — утверждение «не nil». Type — внутренний тип nullable.</summary>
public sealed record BoundNonNull(BoundExpr Operand, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

