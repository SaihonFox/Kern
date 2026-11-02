using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.CodeGen;

/// <summary>Цепочки сравнений: один раз вычислить средние операнды и коротко замкнуть.</summary>
public sealed partial class CodeGenerator
{
	private void EmitComparisonChain(BoundComparisonChainExpression chain)
	{
		// 0 < x < 10  =>  0 < x && x < 10, но x вычисляется один раз:
		// первый средний операнд сохраняется во временный слот и переиспользуется.
		int slot = AllocateLocalSlot();
		EmitExpression(chain.Operands[0]);
		EmitExpression(chain.Operands[1]);
		Emit(OpCode.StoreLocal, slot);
		Emit(OpCode.LoadLocal, slot);

		var failures = new List<int>();
		EmitComparisonOnStack(chain.Operators[0], chain.Span);
		failures.Add(EmitJump(OpCode.JmpIfNot));

		for (int i = 1; i < chain.Operators.Count; i++)
		{
			// temp opN next: слева — значение из предыдущего шага, справа — новый операнд.
			Emit(OpCode.LoadLocal, slot);
			EmitExpression(chain.Operands[i + 1]);
			Emit(OpCode.StoreLocal, slot);
			Emit(OpCode.LoadLocal, slot);
			EmitComparisonOnStack(chain.Operators[i], chain.Span);
			failures.Add(EmitJump(OpCode.JmpIfNot));
		}

		Emit(OpCode.PushTrue);
		var end = EmitJump(OpCode.Jmp);
		foreach (int failure in failures)
			Patch(failure, Position);
		Emit(OpCode.PushFalse);
		Patch(end, Position);
	}

	private void EmitComparisonOnStack(string symbol, TextSpan span)
	{
		Emit(symbol switch
		{
			"<" => OpCode.Lt,
			"<=" => OpCode.Le,
			">" => OpCode.Gt,
			">=" => OpCode.Ge,
			_ => UnknownComparison(symbol, span),
		});
	}

	private OpCode UnknownComparison(string symbol, TextSpan span)
	{
		Report("KRN2201", $"Оператор '{symbol}' не является сравнением", span);
		return OpCode.Lt;
	}
}
