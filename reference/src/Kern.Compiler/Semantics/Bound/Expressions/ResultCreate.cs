using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Bound.Expressions;

/// <summary>`Ok(v)` / `Err(e)`: создание значения result. Value == null — `Ok()` для result&lt;void, E&gt;.</summary>
public sealed record BoundResultCreate(bool IsOk, BoundExpr? Value, TypeSymbol Type, TextSpan Span) : BoundExpr(Type, Span);
