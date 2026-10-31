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

	/// <summary>Тип <c>void</c>.</summary>
	private static bool IsVoid(KernType type) => type is PrimitiveType { Primitive: PrimitiveKind.Void };

	/// <summary>Копирование значения <c>unit</c> при присваивании/передаче (spec §9).</summary>
	private void EmitCopyIfUnit(KernType type)
	{
		if (type is UnitType)
			Emit(OpCode.CopyUnit);
	}

	/// <summary>
	/// Проверка истинности значения (spec §3): <c>nil</c>/<c>none</c> — ложь, остальное — истина.
	/// <c>(v == nil) || (v == none)</c>; Swap сохраняет v для второй проверки.
	/// </summary>
	private void EmitTruthiness(BoundTruthinessExpression expression)
	{
		EmitExpression(expression.Operand);
		Emit(OpCode.Dup);
		Emit(OpCode.PushNil);
		Emit(OpCode.Eq);
		Emit(OpCode.Swap);
		Emit(OpCode.PushNone);
		Emit(OpCode.Eq);
		Emit(OpCode.Or);
		if (!expression.Negated)
			Emit(OpCode.Not);
	}

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
