using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Запись в захваченную переменную через ячейку (cells): замыкание хранит <c>KCell</c>,
/// поэтому изменение видно и объемлющей функции, и другим замыканиям.
/// </summary>
public sealed partial class Binder
{
	private BoundStatement BindCapturedAssignment(AssignmentStatement assignment, BoundVariable variable)
	{
		if (_lambdaFrames.Count == 0)
		{
			Report("KRN2109", $"Переменная '{variable.Name}' недоступна в этом кадре", assignment.Target.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		BoundExpression value;
		if (assignment.Operator == "=")
		{
			value = BindExpression(assignment.Value);
		}
		else
		{
			var context = _lambdaFrames.Peek();
			var index = CaptureIn(context, variable, assignment.Target.Span, out var captureType);
			var current = new BoundCaptureExpression(index, captureType, assignment.Target.Span, IsCell: true);
			var symbol = assignment.Operator.TrimEnd('=');
			value = BindBinaryOperator(symbol, current, BindExpression(assignment.Value), assignment.Span);
			if (value is BoundErrorExpression)
				return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		var store = CapturedStore(variable, value, assignment.Span);
		return store ?? new BoundBlock([], null, FallsThrough: true, assignment.Span);
	}

	/// <summary>Запись значения в ячейку захваченной переменной из текущей лямбды.</summary>
	private BoundStatement? CapturedStore(BoundVariable variable, BoundExpression value, TextSpan span)
	{
		if (_lambdaFrames.Count == 0 || variable.Frame == _frame)
			return null;

		var context = _lambdaFrames.Peek();
		var index = CaptureIn(context, variable, span, out var type);
		var converted = Convert(value, type, span);
		if (IsError(converted.Type))
			return new BoundBlock([], null, FallsThrough: true, span);
		return new BoundCapturedAssignment(index, type, converted, span);
	}
}
