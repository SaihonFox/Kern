using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Bound.Patterns;
using Kern.Compiler.Semantics.Types;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.CodeGen;

internal sealed partial class FunctionCompiler
{
	/// <summary>
	/// Контекст цикла. SpSlot — локальная переменная, где в начале итерации запомнена глубина стека: `stop`/`next` внутри
	/// выражения с недоеденными операндами (`f(1, if c { stop } else 2)`) сначала восстанавливают её. Jumped — был ли такой переход
	/// (если нет, SaveSp заменяется на Nop и убирается оптимизатором).
	/// </summary>
	private sealed record LoopCtx(Label Next, Label Stop)
	{
		public int SpSlot { get; init; } = -1;
		public bool Jumped { get; set; }
		/// <summary>Сколько `use`-переменных было активно при входе в тело: `stop`/`next` освобождают всё, что объявлено глубже.</summary>
		public int DisposeMark { get; init; }
	}

	/// <summary>Начало итерации: SaveSp (позже может превратиться в Nop) и контекст для тела.</summary>
	private LoopCtx BeginLoopBody(Label next, Label stop, out int saveAt)
	{
		int slot = AllocTemp();
		saveAt = B.Position;
		B.Emit(OpCode.SaveSp, slot);
		var ctx = new LoopCtx(next, stop) { SpSlot = slot, DisposeMark = _disposables.Count };
		_loops.Push(ctx);
		return ctx;
	}

	private void EndLoopBody(LoopCtx ctx, int saveAt)
	{
		_loops.Pop();
		if (!ctx.Jumped) B.Overwrite(saveAt, (int)OpCode.Nop, (int)OpCode.Nop);
	}
	private readonly Stack<LoopCtx> _loops = new();

	/// <summary>Прыжок в <paramref name="falseLabel"/>, если условие ложно; `a && b` разворачивается без значений на стеке.</summary>
	private void EmitCondFalse(BoundExpr cond, Label falseLabel)
	{
		if (cond is BoundBinary { Op: Syn.BinaryOp.And } and)
		{
			EmitCondFalse(and.Left, falseLabel);
			EmitCondFalse(and.Right, falseLabel);
			return;
		}
		Emit(cond);
		B.Jump(OpCode.JmpIfNot, falseLabel);
	}

	private void EmitIf(BoundIf i)
	{
		if (i.Else is null) { EmitIfNoElse(i, push: true); return; }
		var elseL = new Label(); var end = new Label();
		EmitCondFalse(i.Condition, elseL);
		Emit(i.Then);
		B.Jump(OpCode.Jmp, end);
		B.Mark(elseL);
		Emit(i.Else);
		B.Mark(end);
	}

	private void EmitIfNoElse(BoundIf i, bool push)
	{
		var end = new Label();
		EmitCondFalse(i.Condition, end);
		EmitDiscard(i.Then);
		B.Mark(end);
		if (push) B.Emit(OpCode.LdVoid);
	}

	// ------------------------------------------------------------ циклы

	/// <summary>`async loop x in stream body`: цикл по `next() -> task&lt;T?&gt;`, nil завершает поток.</summary>
	private void EmitAsyncLoop(BoundLoop lp)
	{
		bool collect = lp.Type is ListType;
		int result = -1;
		if (collect)
		{
			result = AllocTemp();
			B.Emit(OpCode.NewList, G.Sig(((ListType)lp.Type).Element), 0);
			B.Emit(OpCode.StLoc, result);
		}
		int stream = AllocTemp();
		int current = AllocTemp();
		Emit(lp.Iterable);
		B.Emit(OpCode.StLoc, stream);

		var next = new Label(); var exhausted = new Label(); var end = new Label();
		B.Mark(next);
		Mark(lp.Span);
		B.Emit(OpCode.LdLoc, stream);
		EmitInvoke(lp.Iterator!, 0, OwnerArgs(lp.Iterable.Type), [], hasReceiver: true, nonVirtual: false);
		if (lp.IsAsync) B.Emit(OpCode.Await);
		B.Emit(OpCode.StLoc, current);
		B.Emit(OpCode.LdLoc, current);
		B.Jump(OpCode.JmpIfNil, exhausted);
		B.Emit(OpCode.Pop);
		switch (lp.Variable)
		{
			case BoundBindingPattern b: B.Emit(OpCode.LdLoc, current); StoreBinding(b); break;
			case BoundWildcardPattern: break;
			default:
				EmitPattern(lp.Variable, () => B.Emit(OpCode.LdLoc, current), PanicLabel("async loop: элемент не соответствует шаблону переменной"));
				break;
		}
		var ctx = BeginLoopBody(next, end, out int saveAt);
		if (collect)
		{
			EmitCopy(lp.Body);
			B.Emit(OpCode.LdLoc, result);
			B.Emit(OpCode.Swap);
			B.Emit(OpCode.ListAdd);
			B.Emit(OpCode.Pop);
		}
		else EmitDiscard(lp.Body);
		EndLoopBody(ctx, saveAt);
		B.Jump(OpCode.Jmp, next);
		B.Mark(exhausted);
		B.Emit(OpCode.Pop);   // nil, оставшийся после JmpIfNil
		B.Mark(end);
		if (collect) B.Emit(OpCode.LdLoc, result); else B.Emit(OpCode.LdVoid);
	}

	private void EmitLoop(BoundLoop lp)
	{
		if (lp.IsAsync || lp.Kind == LoopKind.Stream) { EmitAsyncLoop(lp); return; }
		bool collect = lp.Type is ListType;
		int result = -1;
		if (collect)
		{
			result = AllocTemp();
			B.Emit(OpCode.NewList, G.Sig(((ListType)lp.Type).Element), 0);
			B.Emit(OpCode.StLoc, result);
		}
		Emit(lp.Iterable);
		if (lp.Kind == LoopKind.Custom && lp.Iterator is { } iter)
			EmitInvoke(iter, 0, OwnerArgs(lp.Iterable.Type), [], hasReceiver: true, nonVirtual: false);
		B.Emit(OpCode.IterInit);

		var next = new Label(); var stop = new Label(); var end = new Label();
		B.Mark(next);
		Mark(lp.Span);
		B.Jump(OpCode.IterNext, end);
		switch (lp.Variable)
		{
			case BoundBindingPattern b: StoreBinding(b); break;
			case BoundWildcardPattern: B.Emit(OpCode.Pop); break;
			default:
			{
				int tmp = AllocTemp();
				B.Emit(OpCode.StLoc, tmp);
				EmitPattern(lp.Variable, () => B.Emit(OpCode.LdLoc, tmp), PanicLabel("цикл: элемент не соответствует шаблону переменной"));
				break;
			}
		}
		var ctx = BeginLoopBody(next, stop, out int saveAt);
		if (collect)
		{
			EmitCopy(lp.Body);
			B.Emit(OpCode.LdLoc, result);
			B.Emit(OpCode.Swap);
			B.Emit(OpCode.ListAdd);
			B.Emit(OpCode.Pop);
		}
		else EmitDiscard(lp.Body);
		EndLoopBody(ctx, saveAt);
		B.Jump(OpCode.Jmp, next);
		B.Mark(stop);
		B.Emit(OpCode.Pop);   // итератор
		B.Mark(end);
		if (collect) B.Emit(OpCode.LdLoc, result); else B.Emit(OpCode.LdVoid);
	}

	/// <summary>`loop cond body` и `loop { … }`: на стеке между итерациями ничего не держим, поэтому stop и next — просто переходы.</summary>
	private void EmitWhile(BoundWhile w)
	{
		bool collect = w.Type is ListType;
		int result = -1;
		if (collect)
		{
			result = AllocTemp();
			B.Emit(OpCode.NewList, G.Sig(((ListType)w.Type).Element), 0);
			B.Emit(OpCode.StLoc, result);
		}

		var next = new Label(); var end = new Label();
		var start = new Label();
		B.Mark(w.PostCondition ? start : next);
		Mark(w.Span);
		var ctx = BeginLoopBody(next, end, out int saveAt);
		if (!w.PostCondition && w.Condition is { } cond) EmitCondFalse(cond, end);
		if (collect)
		{
			EmitCopy(w.Body);
			B.Emit(OpCode.LdLoc, result);
			B.Emit(OpCode.Swap);
			B.Emit(OpCode.ListAdd);
			B.Emit(OpCode.Pop);
		}
		else EmitDiscard(w.Body);
		EndLoopBody(ctx, saveAt);
		if (w.PostCondition && w.Condition is { } post)
		{
			B.Mark(next);          // `next` в do-while — переход к проверке условия
			EmitCondFalse(post, end);
			B.Jump(OpCode.Jmp, start);
		}
		else B.Jump(OpCode.Jmp, next);
		B.Mark(end);
		if (collect) B.Emit(OpCode.LdLoc, result); else B.Emit(OpCode.LdVoid);
	}

	private void EmitLoopJump(bool stop)
	{
		if (_loops.Count == 0) throw new InvalidOperationException("stop/next вне цикла в генераторе");
		var ctx = _loops.Peek();
		ctx.Jumped = true;
		EmitDisposals(ctx.DisposeMark);
		B.Emit(OpCode.RestoreSp, ctx.SpSlot);
		B.Jump(OpCode.Jmp, stop ? ctx.Stop : ctx.Next);
	}

	// ------------------------------------------------------------ match

	private void EmitMatch(BoundMatch m)
	{
		int subject = -1;
		if (m.Target is { } t)
		{
			Emit(t);
			subject = AllocTemp();
			B.Emit(OpCode.StLoc, subject);
		}
		var end = new Label();
		foreach (var arm in m.Arms)
		{
			Mark(arm.Span);
			var next = new Label();
			if (subject >= 0) EmitPattern(arm.Pattern, () => B.Emit(OpCode.LdLoc, subject), next);
			if (arm.Guard is { } g) EmitCondFalse(g, next);
			Emit(arm.Body);
			B.Jump(OpCode.Jmp, end);
			B.Mark(next);
		}
		B.Emit(OpCode.LdStr, M.Intern("match: ни одна ветка не подошла"));
		B.Emit(OpCode.Panic);
		B.Mark(end);
	}
}
