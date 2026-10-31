using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Bound.Statements;

namespace Kern.Compiler.CodeGen;

internal sealed partial class FunctionCompiler
{
	private void EmitStatement(BoundStmt s)
	{
		Mark(s.Span);
		switch (s)
		{
			case BoundExpressionStatement es:
				EmitDiscard(es.Expression);
				break;
			case BoundLocalDeclaration { Initializer: BoundLambda selfLam } rec
				when selfLam.Captures.Any(c => ReferenceEquals(c.Variable, rec.Local)):
				// рекурсивная локальная функция: ячейка создаётся до замыкания
				B.Emit(OpCode.LdNone);
				DeclareAndStore(rec.Local);
				EmitCopy(rec.Initializer!);
				StoreVar(rec.Local);
				break;
			case BoundLocalDeclaration d:
				if (d.Initializer is null) B.Emit(OpCode.LdNone);
				else EmitCopy(d.Initializer);
				DeclareAndStore(d.Local);
				if (d.Dispose is { } disp) _disposables.Add(disp);
				break;
			case BoundDestructuring ds:
			{
				EmitCopy(ds.Value);
				int tmp = AllocTemp();
				B.Emit(OpCode.StLoc, tmp);
				var fail = PanicLabel("деструктуризация не удалась: значение не соответствует шаблону");
				EmitPattern(ds.Pattern, () => B.Emit(OpCode.LdLoc, tmp), fail);
				break;
			}
			default:
				throw new NotSupportedException($"оператор {s.GetType().Name} не поддерживается генератором");
		}
	}

	/// <summary>Вычисляет выражение ради побочных эффектов: на стеке ничего не остаётся.</summary>
	private void EmitDiscard(BoundExpr e)
	{
		switch (e)
		{
			case BoundAssignment a: EmitAssignment(a, keep: false); return;
			case BoundCompoundAssignment c: EmitCompound(c, keep: false); return;
			case BoundBlock b: EmitBlock(b, discard: true); return;
			case BoundIf i when i.Else is null: EmitIfNoElse(i, push: false); return;
			case BoundReturn or BoundStop or BoundNext: Emit(e); return;
			case BoundNumberLiteral or BoundStringLiteral or BoundBoolLiteral or BoundNilLiteral or BoundNoneLiteral
				or BoundCharLiteral or BoundLocal or BoundParameter or BoundThis:
				return;
			default:
				Emit(e);
				B.Emit(OpCode.Pop);
				return;
		}
	}

	/// <summary>Активные `use`-переменные (вызовы dispose) текущей функции, в порядке объявления.</summary>
	private readonly List<BoundExpr> _disposables = [];

	/// <summary>Вызывает dispose() у `use`-переменных с индекса <paramref name="from"/> в обратном порядке; стек не меняется.</summary>
	private void EmitDisposals(int from)
	{
		for (int i = _disposables.Count - 1; i >= from; i--) EmitDiscard(_disposables[i]);
	}

	private void EmitBlock(BoundBlock b, bool discard)
	{
		int mark = _disposables.Count;
		foreach (var s in b.Statements) EmitStatement(s);
		if (b.Result is { } r)
		{
			if (discard) EmitDiscard(r); else Emit(r);
		}
		else if (!discard) B.Emit(OpCode.LdVoid);
		// значение блока уже на стеке — вызовы dispose стек-нейтральны
		if (_disposables.Count > mark)
		{
			EmitDisposals(mark);
			_disposables.RemoveRange(mark, _disposables.Count - mark);
		}
	}
}
