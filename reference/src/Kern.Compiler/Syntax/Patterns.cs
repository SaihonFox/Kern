using Kern.Core;

namespace Kern.Compiler.Syntax;

/// <summary>
/// Паттерн (match, loop-переменная, деструктуризация).
/// Одиночный идентификатор в паттерне — всегда <see cref="BindingPattern"/> (связывание);
/// для сравнения со значением переменной используется guard.
/// </summary>
public abstract record Pattern(TextSpan Span) : Node(Span);

/// <summary>`_`</summary>
public sealed record WildcardPattern(TextSpan Span) : Pattern(Span);

/// <summary>
/// `v` — связывание значения с именем (`v if v > 90 => ...`). В голове `loop` голое имя (<see cref="Declares"/> = false)
/// — присваивание уже объявленной переменной; объявляют `loop i32 i in …` и `loop var i in …` (<see cref="FromVar"/>).
/// </summary>
public sealed record BindingPattern(string Name, TextSpan Span) : Pattern(Span)
{
	/// <summary>true — объявляет новую переменную; false — присваивает существующей (только в `loop`).</summary>
	public bool Declares { get; init; } = true;
	/// <summary>Записано как `var имя`.</summary>
	public bool FromVar { get; init; }
}

/// <summary>
/// Значение: литерал, диапазон, константное выражение, `Color.red`.
/// В `match { cond -> ... }` (чистые guards) условие тоже хранится как ValuePattern.
/// </summary>
public sealed record ValuePattern(Expr Value, TextSpan Span) : Pattern(Span);

/// <summary>`i32 n`, `Circle c`, `str s`; Name == null — проверка типа без связывания (`loop i32 _ in ...`).</summary>
public sealed record TypePattern(TypeRef Type, string? Name, TextSpan Span) : Pattern(Span);

/// <summary>
/// Деструктуризация: `Point(x, y)` (Type != null) или кортеж/коллекция `(i32 a, _, list<i32> ..rest)` (Type == null).
/// </summary>
public sealed record DestructurePattern(TypeRef? Type, IReadOnlyList<Pattern> Elements, TextSpan Span) : Pattern(Span);

/// <summary>Именованное поле в структурном паттерне: `Point(x: 0, y: var y)`; не перечисленные поля — `_`.</summary>
public sealed record NamedPattern(string Name, Pattern Inner, TextSpan Span) : Pattern(Span);

/// <summary>`list<i32> ..rest` / `..rest` / `..` — остаток коллекции.</summary>
public sealed record RestPattern(TypeRef? Type, string? Name, TextSpan Span) : Pattern(Span);

/// <summary>Ветка match: `pattern [if guard] => body`.</summary>
public sealed record MatchArm(Pattern Pattern, Expr? Guard, Expr Body, TextSpan Span) : Node(Span);
