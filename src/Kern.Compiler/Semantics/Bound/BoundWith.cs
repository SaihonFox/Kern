using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Bound;

/// <summary>Замена поля в <c>with</c>-выражении: слот известен после раскладки класса.</summary>
public sealed record BoundWithAssignment(FieldSymbol Field, BoundExpression Value);

/// <summary>
/// <c>x:{ a = 1 }</c> (spec round 27): копия значения с заменёнными полями. Codegen клонирует
/// объект и пишет поля через <c>StoreField</c>; исходное значение не меняется.
/// </summary>
public sealed record BoundWithExpression(
	BoundExpression Target,
	IReadOnlyList<BoundWithAssignment> Assignments,
	TextSpan Span) : BoundExpression(Target.Type, Span);
