using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// Loop-выражение (spec §3): тело выполняется в цикле, значения тела накапливаются в
/// <c>list&lt;T&gt;</c> в скрытом слоте; на выходе список кладётся на стек. Условие/do-while/
/// бесконечный цикл — как у loop-оператора; <c>next</c>/<c>stop</c> работают через общий
/// контекст циклов.
/// </summary>
public sealed partial class CodeGenerator
{
	private void EmitLoopExpression(BoundLoopExpression loop)
	{
		if (loop.ListSlot < 0 ||
			loop.Type is not BuiltinGenericType { BuiltinKind: BuiltinGenericKind.List, Arguments.Count: 1 } list)
		{
			Report("KRN2201", "loop-выражение с результатом-list требует типа list (M3)", loop.Span);
			Emit(OpCode.PushVoid);
			return;
		}

		var signature = _module.InternTypeSig(TypeSigMap.From(list.Arguments[0], _module.Name));
		Emit(OpCode.NewList, signature);
		EmitOperand(0);
		Emit(OpCode.StoreLocal, loop.ListSlot);

		var context = new LoopContext(loop.Label);
		_loops.Push(context);
		try
		{
			if (loop.IsInfinite)
			{
				var start = Position;
				context.ContinueTarget = start;
				EmitLoopExpressionBody(loop);
				EmitJumpTo(start);
				PatchLoop(context);
				Emit(OpCode.LoadLocal, loop.ListSlot);
				return;
			}

			if (loop.IsDoWhile)
			{
				var start = Position;
				EmitLoopExpressionBody(loop);
				context.ContinueTarget = Position;
				EmitExpression(loop.Condition!);
				var repeat = EmitJump(OpCode.JmpIf);
				Patch(repeat, start);
				PatchLoop(context);
				Emit(OpCode.LoadLocal, loop.ListSlot);
				return;
			}

			var conditionStart = Position;
			context.ContinueTarget = conditionStart;
			EmitExpression(loop.Condition!);
			var exit = EmitJump(OpCode.JmpIfNot);
			EmitLoopExpressionBody(loop);
			EmitJumpTo(conditionStart);
			Patch(exit, Position);
			PatchLoop(context);
			Emit(OpCode.LoadLocal, loop.ListSlot);
		}
		finally
		{
			_loops.Pop();
		}
	}

	/// <summary>Тело: значение блока, добавление в список, инкремент итерации.</summary>
	private void EmitLoopExpressionBody(BoundLoopExpression loop)
	{
		EmitBlock(loop.Body, leaveValue: true, loopBody: true);
		Emit(OpCode.LoadLocal, loop.ListSlot);
		Emit(OpCode.Swap);
		Emit(OpCode.ListAppend);
		Emit(OpCode.StoreLocal, loop.ListSlot);

		if (loop.Increment is { } increment)
			EmitStatement(increment, leaveValue: false);
	}
}
