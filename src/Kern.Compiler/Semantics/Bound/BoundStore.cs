using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>
/// Место-корень цепи записи (M3-D4, Q13). Корень либо является местом (локальная
/// переменная, <c>$</c>, статическое поле/свойство), либо ссылкой-классом:
/// ссылка вычисляется один раз и не пишется обратно, а значение-место пишется.
/// </summary>
public abstract record BoundStoreRoot(KernType Type, TextSpan Span) : BoundNode(Span);

/// <summary>Локальная переменная или параметр.</summary>
public sealed record BoundLocalStoreRoot(string Name, int Slot, KernType Type, TextSpan Span)
	: BoundStoreRoot(Type, Span);

/// <summary><c>$</c>: обратная запись идёт в слот приёмника.</summary>
public sealed record BoundSelfStoreRoot(int Slot, KernType Type, TextSpan Span)
	: BoundStoreRoot(Type, Span);

/// <summary>Статическое поле или backing-поле статического автосвойства.</summary>
public sealed record BoundStaticFieldStoreRoot(FieldSymbol Field, KernType Type, TextSpan Span)
	: BoundStoreRoot(Type, Span);

/// <summary>Статическое вычисляемое свойство: чтение getter'ом, запись setter'ом.</summary>
public sealed record BoundStaticPropertyStoreRoot(
	PropertySymbol Property,
	FunctionSymbol Getter,
	FunctionSymbol Setter,
	KernType Type,
	TextSpan Span) : BoundStoreRoot(Type, Span);

/// <summary>Выражение-ссылка (класс): вычисляется один раз, обратной записи нет.</summary>
public sealed record BoundExpressionStoreRoot(BoundExpression Expression, KernType Type, TextSpan Span)
	: BoundStoreRoot(Type, Span);

/// <summary>
/// Звено цепи записи: как прочитать значение из объемлющего объекта и как записать
/// его обратно. Пишется обратно только unit-звено (value semantics, Q13).
/// </summary>
public abstract record BoundStoreLink(KernType Type, TextSpan Span)
{
	public abstract string MemberName { get; }
}

/// <summary>Поле или backing-поле автосвойства.</summary>
public sealed record BoundFieldStoreLink(FieldSymbol Field, KernType Type, TextSpan Span)
	: BoundStoreLink(Type, Span)
{
	public override string MemberName => Field.Name;
}

/// <summary>
/// Вычисляемое свойство: <see cref="Getter"/> читает, <see cref="Writer"/> пишет
/// (<c>set</c> или разрешённый <c>init</c>). У class-звена writer не нужен.
/// </summary>
public sealed record BoundPropertyStoreLink(
	PropertySymbol Property,
	FunctionSymbol? Getter,
	FunctionSymbol? Writer,
	KernType Type,
	TextSpan Span) : BoundStoreLink(Type, Span)
{
	public override string MemberName => Property.Name;
}

/// <summary>
/// Запись через цепь членов <c>a.b.c = v</c> (M3-D4, Q13): каждое unit-звено
/// копируется, изменяется и записывается обратно наружу — в поле, статику,
/// локальную переменную, <c>$</c> или setter свойства.
/// </summary>
public sealed record BoundChainStore(
	BoundStoreRoot Root,
	IReadOnlyList<BoundStoreLink> Links,
	BoundExpression Value,
	TextSpan Span) : BoundStatement(Span);

/// <summary>
/// Текущее значение конечного звена для составного присваивания. Codegen ожидает
/// приёмник на вершине стека и оставляет его там, положив сверху значение звена.
/// </summary>
public sealed record BoundStoreCurrentExpression(
	BoundStoreLink Link,
	KernType Type,
	TextSpan Span) : BoundExpression(Type, Span);
