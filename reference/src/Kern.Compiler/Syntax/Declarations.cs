using Kern.Core;

namespace Kern.Compiler.Syntax;

// ---------- Общие части ----------

/// <summary>Применение атрибута: `#[A(args), B]` даёт по одному AttributeUse на элемент.</summary>
public sealed record AttributeUse(string Name, IReadOnlyList<Argument> Args, TextSpan Span) : Node(Span);

/// <summary>Ограничение generic-параметра.</summary>
public abstract record TypeConstraint(TextSpan Span) : Node(Span);

/// <summary>Ограничение-тип/интерфейс: `T[IHashable]`.</summary>
public sealed record BoundConstraint(TypeRef Type, TextSpan Span) : TypeConstraint(Span);

/// <summary>Ограничение конструктора: `V[ctor(i32, str)]`.</summary>
public sealed record CtorConstraint(IReadOnlyList<TypeRef> ParamTypes, TextSpan Span) : TypeConstraint(Span);

/// <summary>Generic-параметр: `T`, `T[C1, C2]`, `V[ctor()]`.</summary>
public sealed record TypeParam(string Name, IReadOnlyList<TypeConstraint> Constraints, TextSpan Span) : Node(Span);

/// <summary>
/// Параметр функции/конструктора/лямбды. IsVariadic — `i32 ..args`; AutoInit — `ctor(str $name)`
/// (автоприсваивание полю); Mods — модификаторы параметра первичного конструктора (`pub str name`).
/// </summary>
public sealed record Param(
	IReadOnlyList<AttributeUse> Attributes, Modifiers Mods, TypeRef Type, string Name, TextSpan NameSpan,
	Expr? Default, bool IsVariadic, bool AutoInit, TextSpan Span) : Node(Span);

/// <summary>Родитель в списке наследования: `[A]` (Args пуст) или `[A(x * 2)]`.</summary>
public sealed record ParentRef(TypeRef Type, IReadOnlyList<Argument> Args, TextSpan Span) : Node(Span);

// ---------- Объявления ----------

/// <summary>Объявление верхнего уровня или член типа.</summary>
public abstract record Decl(IReadOnlyList<AttributeUse> Attributes, Modifiers Mods, TextSpan Span) : Node(Span);

/// <summary>`use a.b[X, Y]`, `use a.b.*`, `use dotnet.System.IO[File]`. Path без элементов в скобках.</summary>
public sealed record UseDecl(IReadOnlyList<string> Path, IReadOnlyList<string> Items, bool Wildcard, TextSpan Span) : Node(Span)
{
	/// <summary>Импорт из .NET (`use dotnet....`).</summary>
	public bool IsDotnet => Path.Count > 0 && Path[0] == "dotnet";
}

/// <summary>
/// Функция/метод. ParentSelector != null — выбор родителя `fun $[A].foo(...) -> R` (без тела).
/// Абстрактный метод `@fun` — Mods.IsAbstract, Body == null. Static — `#fun`; override — `~`.
/// HasParams — были ли скобки параметров. Body — блок или одно выражение.
/// </summary>
public sealed record FunDecl(
	IReadOnlyList<AttributeUse> Attributes, Modifiers Mods, string Name, TextSpan NameSpan,
	IReadOnlyList<TypeParam> TypeParams, string? ParentSelector, IReadOnlyList<Param> Params, bool HasParams,
	TypeRef? ReturnType, Expr? Body, TextSpan Span) : Decl(Attributes, Mods, Span);

/// <summary>`fun str.shout(...) body` — расширение метода; внутри `$` — получатель.</summary>
public sealed record ExtensionFunDecl(
	IReadOnlyList<AttributeUse> Attributes, Modifiers Mods, TypeRef Receiver, string Name, TextSpan NameSpan,
	IReadOnlyList<TypeParam> TypeParams, IReadOnlyList<Param> Params, TypeRef? ReturnType, Expr Body, TextSpan Span)
	: Decl(Attributes, Mods, Span);

/// <summary>`i32 str.len2 body` — расширение свойства.</summary>
public sealed record ExtensionPropDecl(
	IReadOnlyList<AttributeUse> Attributes, Modifiers Mods, TypeRef Type, TypeRef Receiver, string Name, TextSpan NameSpan,
	Expr Body, TextSpan Span) : Decl(Attributes, Mods, Span);

/// <summary>`op i32.*(str s) -> str body` — внешний оператор. Symbol — текст оператора (`+`, `*`, `==`, `in`...).</summary>
public sealed record ExtensionOpDecl(
	IReadOnlyList<AttributeUse> Attributes, Modifiers Mods, TypeRef Receiver, string Symbol,
	IReadOnlyList<Param> Params, TypeRef? ReturnType, Expr Body, TextSpan Span) : Decl(Attributes, Mods, Span);

/// <summary>`op +(Point other) -> Point body` — оператор внутри типа.</summary>
public sealed record OpDecl(
	IReadOnlyList<AttributeUse> Attributes, Modifiers Mods, string Symbol,
	IReadOnlyList<Param> Params, TypeRef? ReturnType, Expr Body, TextSpan Span) : Decl(Attributes, Mods, Span);

/// <summary>`ctor(args) body` — вторичный конструктор. <see cref="Chain"/> — `ctor(i32 x) : $(x, 0)`: вызов другого конструктора этого типа.</summary>
public sealed record CtorDecl(
	IReadOnlyList<AttributeUse> Attributes, Modifiers Mods, IReadOnlyList<Param> Params, Expr? Body, TextSpan Span,
	IReadOnlyList<Argument>? Chain = null)
	: Decl(Attributes, Mods, Span);

/// <summary>
/// Поле: `i32 x`, `i32 x = 5`, `!str name` (Mods.IsRequired), `#i32 count` (Mods.IsStatic),
/// `str $[A].name` (ParentSelector). Type == null — `var x = ...`. Может быть и на верхнем уровне.
/// </summary>
public sealed record FieldDecl(
	IReadOnlyList<AttributeUse> Attributes, Modifiers Mods, TypeRef? Type, string Name, TextSpan NameSpan,
	string? ParentSelector, Expr? Init, TextSpan Span) : Decl(Attributes, Mods, Span);

public enum AccessorKind { Get, Set, Init }

/// <summary>
/// Аксессор свойства. Body == null — автоаксессор (`get;`). Access != Default — `priv set`.
/// ParamName — `set(value) ...`. Тело — блок или выражение (`get expr` — «ленивая» форма).
/// </summary>
public sealed record Accessor(AccessorKind Kind, Access Access, string? ParamName, Expr? Body, TextSpan Span) : Node(Span);

/// <summary>
/// Свойство: `i32 val { get _val; set(value) _val = value * 2 }`, автосвойства `{ get; set }`, `{ get; init }`,
/// `{ get; priv set }`, а также форма с единственным аксессором без скобок (`f64 len get expr`).
/// Init — начальное значение `{ get; set } = 5`.
/// </summary>
public sealed record PropertyDecl(
	IReadOnlyList<AttributeUse> Attributes, Modifiers Mods, TypeRef Type, string Name, TextSpan NameSpan,
	string? ParentSelector, IReadOnlyList<Accessor> Accessors, Expr? Init, TextSpan Span) : Decl(Attributes, Mods, Span);

public enum TypeKind
{
	Type,           // type
	AbstractType,   // @type
	Unit,           // unit
	ImmutableUnit,  // unit!
	Attribute,      // attribute
}

/// <summary>
/// `type X<T>(params)[Parent(args), Other] { members }`. PrimaryCtor == null — первичного конструктора нет
/// (`type X`), пустой список — `type X()`. Первичный конструктор идёт ДО списка родителей.
/// </summary>
public sealed record TypeDecl(
	IReadOnlyList<AttributeUse> Attributes, Modifiers Mods, TypeKind Kind, string Name, TextSpan NameSpan,
	IReadOnlyList<TypeParam> TypeParams, IReadOnlyList<Param>? PrimaryCtor, IReadOnlyList<ParentRef> Parents,
	IReadOnlyList<Decl> Members, TextSpan Span) : Decl(Attributes, Mods, Span);

/// <summary>`type Shape = [Circle, Rect]` — union-алиас.</summary>
public sealed record UnionAliasDecl(
	IReadOnlyList<AttributeUse> Attributes, Modifiers Mods, string Name, TextSpan NameSpan,
	IReadOnlyList<TypeParam> TypeParams, UnionType Union, TextSpan Span) : Decl(Attributes, Mods, Span);

/// <summary>Элемент enum: `red`, `ok(200)`, `green = 5`.</summary>
public sealed record EnumEntry(string Name, TextSpan NameSpan, IReadOnlyList<Argument> Args, Expr? Value, TextSpan Span) : Node(Span);

/// <summary>
/// `enum Name(params)[Parents] { entry, entry(args), entry = value; members }` —
/// элементы через запятую/перевод строки; после `;` могут идти члены (поля, методы).
/// </summary>
public sealed record EnumDecl(
	IReadOnlyList<AttributeUse> Attributes, Modifiers Mods, string Name, TextSpan NameSpan,
	IReadOnlyList<Param>? PrimaryCtor, IReadOnlyList<ParentRef> Parents,
	IReadOnlyList<EnumEntry> Entries, IReadOnlyList<Decl> Members, TextSpan Span) : Decl(Attributes, Mods, Span);
