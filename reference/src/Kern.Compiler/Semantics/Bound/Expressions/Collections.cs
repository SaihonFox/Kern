using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Bound.Expressions;

/// <summary>`[1, 2]` / `&lt;i32&gt;[1, 2]`. Type — <see cref="ListType"/>; элементы уже приведены к типу элемента. Также упаковка variadic-аргументов.</summary>
public sealed record BoundList(IReadOnlyList<BoundExpr> Elements, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span)
{
	public TypeSymbol ElementType => (Type as ListType)?.Element ?? ErrorType.Instance;
}

/// <summary>`array&lt;T&gt;[1, 2]`. Type — <see cref="ArrayType"/>.</summary>
public sealed record BoundArray(IReadOnlyList<BoundExpr> Elements, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span)
{
	public TypeSymbol ElementType => (Type as ArrayType)?.Element ?? ErrorType.Instance;
}

public sealed record BoundDictEntry(BoundExpr Key, BoundExpr Value, TextSpan Span) : BoundNode(Value.Type, Span);

/// <summary>`["k": 1]` / `&lt;str: i32&gt;[...]`. Type — <see cref="DictType"/>.</summary>
public sealed record BoundDict(IReadOnlyList<BoundDictEntry> Entries, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>`(1, "a")` / `(x: 1, y: 2)`. Type — <see cref="TupleType"/> (имена элементов лежат в нём).</summary>
public sealed record BoundTuple(IReadOnlyList<BoundExpr> Elements, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>
/// `a..b`, `a..=b`, `a..b:step`; границы могут отсутствовать. Границы приведены к <see cref="ElementType"/>.
/// Type — тип диапазона из std (нативный тип или ErrorType, пока он не определён).
/// </summary>
public sealed record BoundRange(
	BoundExpr? Start, BoundExpr? End, bool Inclusive, BoundExpr? Step, TypeSymbol ElementType, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);
