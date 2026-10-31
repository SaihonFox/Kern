using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Безопасный доступ <c>obj?.member</c> (spec §3): приёмник вычисляется один раз и
/// сохраняется во временную локальную; при <c>nil</c> результатом становится <c>nil</c>,
/// иначе выполняется обычный доступ. Тип результата — <c>Nullable(T)</c> (у <c>void</c>
/// остаётся <c>void</c>).
/// </summary>
public sealed partial class Binder
{
	private BoundExpression BindConditionalAccess(
		BoundExpression receiver,
		NullableType nullable,
		TextSpan span,
		Func<BoundExpression, BoundExpression> bindAccess)
	{
		var temporary = DeclareLocal($"#opt{_nextSlot}", nullable.Underlying, false, receiver.Span);
		DeclareFlow(temporary, true, nullable.Underlying);

		var access = bindAccess(new BoundVariableExpression(
			temporary.Name,
			temporary.Slot,
			nullable.Underlying,
			receiver.Span));
		if (IsError(access.Type))
			return new BoundErrorExpression(span);

		var type = IsVoid(access.Type) ? PrimitiveType.Void : NullableType.Create(access.Type);
		return new BoundConditionalAccessExpression(receiver, temporary.Slot, access, type, span);
	}
}
