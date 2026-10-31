using Kern.Compiler.Semantics.Bound.Patterns;
using Kern.Compiler.Semantics.Symbols;
using Kern.Core;

namespace Kern.Compiler.Semantics.Bound.Statements;

/// <summary>Выражение, значение которого отбрасывается.</summary>
public sealed record BoundExpressionStatement(BoundExpr Expression, TextSpan Span) : BoundStmt(Span);

/// <summary>
/// Объявление локальной переменной `T x = e` / `var x = e` / `T x` / `T x(args)`.
/// <see cref="Initializer"/> уже приведён к типу переменной; для `T x(args)` это <c>BoundNew</c>;
/// null — переменная в состоянии `none` (до первого присваивания).
/// </summary>
public sealed record BoundLocalDeclaration(LocalSymbol Local, BoundExpr? Initializer, TextSpan Span, BoundExpr? Dispose = null) : BoundStmt(Span);
// Dispose != null — объявление `use T x = e`: вызов `x.dispose()` выполняется при выходе из блока (в т.ч. по ret/stop/next).

/// <summary>`(i32 a, _, list&lt;i32&gt; ..rest) = e` и `Point(x, y) = e`: <see cref="Pattern"/> должен быть неопровержимым.</summary>
public sealed record BoundDestructuring(BoundDestructurePattern Pattern, BoundExpr Value, IReadOnlyList<LocalSymbol> Locals, TextSpan Span) : BoundStmt(Span);
