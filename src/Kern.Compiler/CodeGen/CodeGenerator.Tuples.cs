using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// Кортежи (spec §3): литерал собирается <c>NewTuple</c> из элементов, вычисленных по
/// порядку (первый ниже остальных), элемент читается <c>TupleGet</c> по индексу.
/// Имена элементов живут только в типе и не попадают на стек.
/// </summary>
public sealed partial class CodeGenerator
{
	private void EmitTupleLiteral(BoundTupleExpression literal)
	{
		if (literal.Type is not TupleType tuple || tuple.Elements.Count != literal.Items.Count)
		{
			Report("KRN2201", "Тип литерала кортежа не совпадает с числом элементов", literal.Span);
			Emit(OpCode.PushVoid);
			return;
		}

		foreach (BoundExpression item in literal.Items)
		{
			EmitExpression(item);
			EmitCopyIfUnit(item.Type);
		}

		var signature = _module.InternTypeSigList(
			tuple.Elements.Select(element => TypeSigMap.From(element.Type, _module.Name)));
		Emit(OpCode.NewTuple, signature);
	}

	private void EmitTupleItem(BoundTupleItemExpression item)
	{
		EmitExpression(item.Target);
		Emit(OpCode.TupleGet, item.Index);
	}
}
