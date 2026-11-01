using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>Связанная переменная паттерна: имя, слот, тип и признак единственного присваивания.</summary>
public sealed record BoundPatternBinding(string Name, int Slot, KernType Type, TextSpan Span);

/// <summary>
/// <c>x is Pattern</c> (spec round 27): проверка паттерна без match-ветвления. Связывания
/// уже записаны codegen'ом в слоты; при <c>false</c> выражение даёт <c>false</c>, а ветвь
/// справа от <c>&amp;&amp;</c>/then не выполняется.
/// </summary>
public sealed record BoundIsPatternExpression(
	BoundExpression Subject,
	BoundPattern Pattern,
	bool Negated,
	IReadOnlyList<BoundPatternBinding> Bindings,
	TextSpan Span) : BoundExpression(PrimitiveType.Bool, Span);

/// <summary>Разобранный паттерн match. Ветви строятся в <c>Binder.Patterns</c>.</summary>
public abstract record BoundPattern(TextSpan Span) : BoundNode(Span);

/// <summary><c>_</c> — совпадает со всем и ничего не связывает.</summary>
public sealed record BoundWildcardPattern(TextSpan Span) : BoundPattern(Span);

/// <summary>Литерал значения: число, строка, char или bool.</summary>
public sealed record BoundLiteralPattern(BoundExpression Value, TextSpan Span) : BoundPattern(Span);

/// <summary>Диапазон значений <c>1..=5</c> / <c>1..5</c> (B4a): обе границы — литералы типа субъекта.</summary>
public sealed record BoundRangePattern(
	BoundExpression Start,
	BoundExpression End,
	bool Inclusive,
	KernType Type,
	TextSpan Span) : BoundPattern(Span);

/// <summary>B4a2: or-паттерн <c>p1 | p2</c> — совпадает любая из альтернатив.</summary>
public sealed record BoundOrPattern(IReadOnlyList<BoundPattern> Alternatives, TextSpan Span) : BoundPattern(Span);

/// <summary>
/// B4b1/B4b2: паттерн списка <c>[p1, p2]</c> / <c>[a, ..rest, z]</c>.
/// <see cref="Items"/> — элементы без rest'а в порядке записи; <see cref="ItemsBeforeRest"/> —
/// сколько из них идёт до rest'а (<c>-1</c> — rest'а нет, длина должна совпасть точно).
/// <see cref="Rest"/> — связанное имя rest'а (<c>..name</c>) с типом <c>list&lt;T&gt;</c>.
/// </summary>
public sealed record BoundListPattern(
	IReadOnlyList<BoundPattern> Items,
	int ItemsBeforeRest,
	KernType ElementType,
	BoundPatternBinding? Rest,
	TextSpan Span) : BoundPattern(Span)
{
	/// <summary>Есть ли в паттерне rest <c>..</c> (именованный или нет).</summary>
	public bool HasRest => ItemsBeforeRest >= 0;
}

/// <summary><c>nil</c>; допустим только для nullable-субъекта.</summary>
public sealed record BoundNilPattern(KernType Type, TextSpan Span) : BoundPattern(Span);

/// <summary>Связывание всего субъекта: <c>x =></c> или <c>var x</c> внутри структурного паттерна.</summary>
public sealed record BoundBindingPattern(string Name, int Slot, KernType Type, TextSpan Span) : BoundPattern(Span);

/// <summary><c>str s</c> / <c>str</c>: проверка типа субъекта и необязательное связывание.</summary>
public sealed record BoundTypePattern(KernType Type, BoundPattern? Binding, TextSpan Span) : BoundPattern(Span);

/// <summary>Один именованный член конструкторского/структурного паттерна.</summary>
public sealed record BoundFieldPattern(string Name, KernType Type, BoundPattern Pattern, TextSpan Span) : BoundNode(Span);

/// <summary>Позиционный паттерн unit-типа: <c>Point(x, y)</c>.</summary>
public sealed record BoundConstructorPattern(
	KernType Type,
	IReadOnlyList<BoundFieldPattern> Fields,
	TextSpan Span) : BoundPattern(Span);

/// <summary>Структурный паттерн unit-типа: <c>Point(x: 0, y: var y)</c>.</summary>
public sealed record BoundStructuralPattern(
	KernType Type,
	IReadOnlyList<BoundFieldPattern> Fields,
	bool HasRest,
	TextSpan Span) : BoundPattern(Span);

/// <summary>Чистое условие в targetless-match: <c>match { x &gt; 0 =&gt; ... }</c>.</summary>
public sealed record BoundGuardPattern(BoundExpression Condition, TextSpan Span) : BoundPattern(Span);

/// <summary>Паттерн, который текущий срез ещё не поддерживает (KRN2114).</summary>
public sealed record BoundUnsupportedPattern(TextSpan Span) : BoundPattern(Span);

/// <summary>Одна ветвь match: паттерн, необязательный guard, тело и связывания паттерна.</summary>
public sealed record BoundMatchArm(
	BoundPattern Pattern,
	BoundExpression? Guard,
	BoundExpression Body,
	IReadOnlyList<BoundPatternBinding> Bindings,
	TextSpan Span) : BoundNode(Span);

/// <summary>
/// Выражение match. <see cref="Target"/> = null для targetless-формы; тип вычисляется по телам
/// ветвей, <c>never</c> не влияет на общий тип.
/// </summary>
public sealed record BoundMatchExpression(
	BoundExpression? Target,
	IReadOnlyList<BoundMatchArm> Arms,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);
