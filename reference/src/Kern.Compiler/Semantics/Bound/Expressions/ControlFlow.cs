using Kern.Compiler.Semantics.Bound.Patterns;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Bound.Expressions;

/// <summary>
/// Блок `{ ... }`: инструкции и значение — последнее выражение (<see cref="Result"/>; null → тип void).
/// <see cref="Locals"/> — переменные, объявленные непосредственно в блоке (для распределения слотов).
/// </summary>
public sealed record BoundBlock(
	IReadOnlyList<BoundStmt> Statements, BoundExpr? Result, IReadOnlyList<LocalSymbol> Locals, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>`if c a else b` и тернарный `c ? a : b`. Else == null → Type = void (ветка без else не даёт значения).</summary>
public sealed record BoundIf(BoundExpr Condition, BoundExpr Then, BoundExpr? Else, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>Ветка match. <see cref="Guard"/> — `if`-условие (bool) после успешного паттерна.</summary>
public sealed record BoundMatchArm(BoundPattern Pattern, BoundExpr? Guard, BoundExpr Body, IReadOnlyList<LocalSymbol> Bindings, TextSpan Span)
	: BoundNode(Body.Type, Span);

/// <summary>
/// `match target { ... }`. <see cref="Target"/> == null — чистые guards `match { x > 0 -> ... }`: в ветках паттерн —
/// <see cref="BoundWildcardPattern"/>, а условие лежит в <see cref="BoundMatchArm.Guard"/>.
/// <see cref="IsExhaustive"/> — доказана ли исчерпываемость (иначе при отсутствии совпадения — panic в рантайме).
/// </summary>
public sealed record BoundMatch(BoundExpr? Target, IReadOnlyList<BoundMatchArm> Arms, bool IsExhaustive, TypeSymbol Type, TextSpan Span)
	: BoundExpr(Type, Span);

/// <summary>Способ обхода в `loop`.</summary>
public enum LoopKind
{
	/// <summary>`a..b` / `a..=b` / `a..b:step` (Iterable — <see cref="BoundRange"/>).</summary>
	Range,
	List,
	Array,
	Dict,
	/// <summary>Нативный/пользовательский итерируемый тип (<see cref="BoundLoop.Iterator"/>), либо async-поток.</summary>
	Custom,
	/// <summary>Тип-наследник iter&lt;T&gt;: цикл вызывает next() до nil.</summary>
	Stream,
}

/// <summary>
/// `loop var in iterable body`. <see cref="Variable"/> — паттерн переменной цикла (привязка, тип-паттерн, `_`, деструктуризация),
/// <see cref="ElementType"/> — тип элементов. <see cref="Iterator"/> — выбранный метод получения итератора для <see cref="LoopKind.Custom"/>.
/// <see cref="IsAsync"/> — `async loop` по async_iter&lt;T&gt;. Type — результат цикла (определяет биндер по телу).
/// </summary>
public sealed record BoundLoop(
	BoundPattern Variable,
	BoundExpr Iterable,
	BoundExpr Body,
	LoopKind Kind,
	TypeSymbol ElementType,
	FunctionSymbol? Iterator,
	bool IsAsync,
	TypeSymbol Type,
	TextSpan Span) : BoundExpr(Type, Span);

/// <summary>
/// `loop cond body` / `loop { … }`. <see cref="Condition"/> == null — бесконечный цикл. Type — результат цикла (list значений тела или void).
/// </summary>
public sealed record BoundWhile(BoundExpr? Condition, BoundExpr Body, TypeSymbol Type, TextSpan Span, bool PostCondition = false) : BoundExpr(Type, Span);

/// <summary>`stop` — выход из ближайшего цикла.</summary>
public sealed record BoundStop(TextSpan Span) : BoundExpr(NeverType.Instance, Span);

/// <summary>`next` — следующая итерация ближайшего цикла.</summary>
public sealed record BoundNext(TextSpan Span) : BoundExpr(NeverType.Instance, Span);

/// <summary>
/// `ret [expr]` — возврат из ближайшей функции или лямбды. <see cref="Value"/> уже приведён к возвращаемому типу
/// функции (null — `ret` без значения в void-функции). Тип выражения — <see cref="NeverType"/>: управление дальше не идёт,
/// поэтому ветка с `ret` не участвует в выводе типа `if`/`match`.
/// </summary>
public sealed record BoundReturn(BoundExpr? Value, TextSpan Span) : BoundExpr(NeverType.Instance, Span);

/// <summary>`await e`. Type — результат задачи (task&lt;T&gt; → T). Оператор допустим только в async-функции/лямбде.</summary>
public sealed record BoundAwait(BoundExpr Operand, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);
