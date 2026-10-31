using Kern.Core;

namespace Kern.Compiler.Syntax;

/// <summary>Синтаксическая ссылка на тип.</summary>
public abstract record TypeRef(TextSpan Span) : Node(Span);

/// <summary>Именованный тип: `i32`, `list<str>`, `a.b.Type<T>`. Name может содержать точки (квалификация).</summary>
public sealed record NamedType(string Name, IReadOnlyList<TypeRef> TypeArgs, TextSpan Span) : TypeRef(Span);

/// <summary>`T?`</summary>
public sealed record NullableType(TypeRef Inner, TextSpan Span) : TypeRef(Span);

/// <summary>`fun(T1, T2) -> R` и `fun -> R`. Return == null — не указан.</summary>
public sealed record FuncType(IReadOnlyList<TypeRef> Params, TypeRef? Return, TextSpan Span) : TypeRef(Span);

/// <summary>Элемент кортежного типа; Name != null для именованного `(i32 x, i32 y)`.</summary>
public sealed record TupleTypeElement(TypeRef Type, string? Name, TextSpan Span) : Node(Span);

/// <summary>`(i32, str)` / `(i32 x, i32 y)`</summary>
public sealed record TupleType(IReadOnlyList<TupleTypeElement> Elements, TextSpan Span) : TypeRef(Span);

/// <summary>Union-тип `[T1, T2]` (правая часть `type A = [T1, T2]`).</summary>
public sealed record UnionType(IReadOnlyList<TypeRef> Members, TextSpan Span) : TypeRef(Span);

/// <summary>`array<T>` (синтаксически выделен отдельно от list/dict).</summary>
public sealed record ArrayType(TypeRef Element, TextSpan Span) : TypeRef(Span);
