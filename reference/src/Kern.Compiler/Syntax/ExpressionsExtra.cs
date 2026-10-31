using Kern.Core;

namespace Kern.Compiler.Syntax;

/// <summary>Постфиксное `expr!` — утверждение «не nil» (non-null assertion).</summary>
public sealed record NonNullExpr(Expr Operand, TextSpan Span) : Expr(Span);

/// <summary>Литерал `none` (внутреннее состояние «значение ещё не присвоено»; используется в сравнениях `x == none`).</summary>
public sealed record NoneLitExpr(TextSpan Span) : Expr(Span);
