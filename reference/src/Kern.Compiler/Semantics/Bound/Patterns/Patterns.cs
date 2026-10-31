using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Bound.Patterns;

/// <summary>`_` — совпадает со всем, ничего не связывает.</summary>
public sealed record BoundWildcardPattern(TypeSymbol Type, TextSpan Span) : BoundPattern(Type, Span);

/// <summary>`v` — связывает всё значение с новой локальной переменной <see cref="Local"/> (её тип = <see cref="BoundNode.Type"/> паттерна).</summary>
/// <param name="IsAssignment">true — присваивание уже объявленной переменной (голое имя в голове `loop`), а не объявление.</param>
public sealed record BoundBindingPattern(LocalSymbol Local, TypeSymbol Type, TextSpan Span, bool IsAssignment = false) : BoundPattern(Type, Span);

/// <summary>
/// Сравнение со значением: литерал, константа, `Color.red`, диапазон (<see cref="BoundRange"/>).
/// <see cref="Equality"/> — выбранный `op ==` (null — встроенное сравнение / проверка вхождения в диапазон).
/// </summary>
public sealed record BoundValuePattern(BoundExpr Value, FunctionSymbol? Equality, TypeSymbol Type, TextSpan Span) : BoundPattern(Type, Span)
{
	public bool IsRange => Value is BoundRange;
}

/// <summary>
/// Проверка типа (`Circle c`, `i32 n`, `str _`), is-паттерн. <see cref="TestType"/> — проверяемый тип,
/// <see cref="Local"/> — связывание суженного значения (null — без связывания).
/// </summary>
public sealed record BoundTypePattern(TypeSymbol TestType, LocalSymbol? Local, TypeSymbol Type, TextSpan Span) : BoundPattern(Type, Span);

/// <summary>Что именно разбирает <see cref="BoundDestructurePattern"/>.</summary>
public enum DestructureKind
{
	/// <summary>Кортеж `(a, b)`.</summary>
	Tuple,
	/// <summary>Коллекция (list/array) по позициям, возможно с <see cref="BoundRestPattern"/>.</summary>
	Collection,
	/// <summary>Объект/unit `Point(x, y)`: подпаттерны сопоставляются <see cref="BoundDestructurePattern.Members"/>.</summary>
	Object,
	/// <summary>`Ok(v)` для значения result&lt;T, E&gt;: один подпаттерн над значением Ok (TestType — тип входа).</summary>
	ResultOk,
	/// <summary>`Err(e)` для значения result&lt;T, E&gt;: один подпаттерн над значением Err.</summary>
	ResultErr,
}

/// <summary>
/// Деструктуризация. <see cref="TestType"/> — проверяемый тип (для `Point(x, y)`; для кортежа/коллекции — тип входа).
/// <see cref="Members"/> — для Object: поля/свойства (<see cref="FieldSymbol"/>/<see cref="PropertySymbol"/>) по позициям подпаттернов
/// (порядок — как у параметров первичного конструктора); иначе пусто.
/// </summary>
public sealed record BoundDestructurePattern(
	DestructureKind Kind,
	TypeSymbol TestType,
	IReadOnlyList<Symbol> Members,
	IReadOnlyList<BoundPattern> Elements,
	TypeSymbol Type,
	TextSpan Span) : BoundPattern(Type, Span);

/// <summary>`..rest` / `list&lt;i32&gt; ..rest` / `..`: остаток коллекции; <see cref="Local"/> — null для `..`. Type — тип остатка.</summary>
public sealed record BoundRestPattern(LocalSymbol? Local, TypeSymbol Type, TextSpan Span) : BoundPattern(Type, Span);
