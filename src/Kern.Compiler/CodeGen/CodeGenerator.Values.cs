using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.CodeGen;

/// <summary>Мелкие предикаты кодогенерации: оставляет ли выражение значение, копирование unit.</summary>
public sealed partial class CodeGenerator
{
	/// <summary>Оставляет ли выражение значение на стеке (иначе его нельзя «снять» через <c>Pop</c>).</summary>
	private static bool LeavesValue(BoundExpression expression) => expression switch
	{
		BoundBlockExpression block => block.FallsThrough,
		BoundIfExpression conditional => conditional.Else is null ||
			LeavesValue(conditional.Then) ||
			LeavesValue(conditional.Else),
		BoundElvisExpression => true,
		BoundReturnExpression => false,
		BoundCallExpression { Type: PrimitiveType { Primitive: PrimitiveKind.Never } } => false,
		_ => true
	};

	/// <summary>
	/// <c>obj?.member</c>: приёмник один раз в слот, проверка на <c>nil</c>, затем обычный
	/// доступ из слота; в ветви <c>nil</c> на стек кладётся <c>nil</c>.
	/// </summary>
	private void EmitConditionalAccess(BoundConditionalAccessExpression access)
	{
		EmitExpression(access.Receiver);
		Emit(OpCode.StoreLocal, access.Slot);
		Emit(OpCode.LoadLocal, access.Slot);
		Emit(OpCode.PushNil);
		Emit(OpCode.Eq);
		var present = EmitJump(OpCode.JmpIfNot);
		Emit(OpCode.PushNil);
		var end = EmitJump(OpCode.Jmp);
		Patch(present, Position);
		EmitExpression(access.Access);
		Patch(end, Position);
	}
}
