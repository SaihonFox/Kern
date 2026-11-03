using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Аргументы <c>&amp;x</c>/<c>*x</c> (spec §6): <c>&amp;</c> — значение только для чтения,
/// <c>*</c> — место (переменная или поле), ячейку которого получит вызываемый.
/// </summary>
public sealed partial class Binder
{
	private BoundExpression BindReferenceArgument(ReferenceArgumentExpression reference)
	{
		BoundExpression operand = BindExpression(reference.Operand);
		if (IsError(operand.Type))
			return new BoundErrorExpression(reference.Span);

		if (!reference.Mutable)
			return new BoundReferenceArgumentExpression(operand, false, reference.Span);

		if (reference.Operand is IdentifierExpression identifier)
		{
			var variable = _scope.Lookup(identifier.Name);
			if (variable is null)
			{
				// Без квалификации это может быть статическое поле своего типа или
				// поле своего экземпляра (`*total` внутри типа).
				if (operand is BoundFieldExpression)
					return new BoundReferenceArgumentExpression(operand, true, reference.Span);
				Report("KRN2103", $"Неизвестная переменная '{identifier.Name}'", identifier.Span);
				return new BoundErrorExpression(reference.Span);
			}
			if (variable.Passing == ParameterPassing.In)
			{
				Report(
					"KRN2101",
					$"Параметр '{variable.Name}' объявлен как '&' (только чтение) и не может быть передан как '*'",
					reference.Span);
				return new BoundErrorExpression(reference.Span);
			}
			if (variable.Frame != _frame)
			{
				// Захваченная переменная живёт в KCell; её и передаём как ячейку.
				if (operand is BoundCaptureExpression { IsCell: true })
					return new BoundReferenceArgumentExpression(operand, true, reference.Span);
				Report(
					"KRN2101",
					$"Захваченную переменную '{variable.Name}' нельзя передать как '*'",
					reference.Span);
				return new BoundErrorExpression(reference.Span);
			}
			if (!variable.IsParameter && variable.IsSingleAssignment)
			{
				Report(
					"KRN2101",
					$"Переменная '{variable.Name}' объявлена как single-assignment и не может быть передана как '*'",
					reference.Span);
				return new BoundErrorExpression(reference.Span);
			}
			return new BoundReferenceArgumentExpression(operand, true, reference.Span);
		}

		if (reference.Operand is MemberAccessExpression)
		{
			if (operand is BoundFieldExpression field)
			{
				if (field.Receiver is { } temporary &&
					temporary.Type is UnitType { IsImmutable: false } &&
					!IsPlaceReceiver(temporary))
				{
					Report("KRN2106", "Нельзя взять '*' у поля временного unit-значения", reference.Span);
					return new BoundErrorExpression(reference.Span);
				}
				return new BoundReferenceArgumentExpression(operand, true, reference.Span);
			}

			Report("KRN2101", "Маркер '*' применим только к полю, а не к свойству или методу", reference.Span);
			return new BoundErrorExpression(reference.Span);
		}

		Report("KRN2101", "Маркер '*' применим только к переменной или полю", reference.Span);
		return new BoundErrorExpression(reference.Span);
	}
}
