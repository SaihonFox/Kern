using Kern.Compiler.Semantics.Types;
using Kern.Core;

namespace Kern.Compiler.Semantics.Bound.Expressions;

/// <summary>
/// `typeof&lt;T&gt;()` → опкод TypeOfSig с подписью <see cref="Target"/> (reified: T подставляется в рантайме). Type — <c>type</c>.
/// `typeof(str name)` узлом НЕ является: это обычный <see cref="BoundCall"/> выбранной перегрузки `typeof(...)`.
/// </summary>
public sealed record BoundTypeOf(TypeSymbol Target, TextSpan Span) : BoundExpr(PrimitiveType.Type, Span);
