using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Многоцелевое присваивание и swap <c>a, b = b, a</c> (spec §3): значения правой части
/// считаются слева направо во временные локальные, затем записываются в цели. Временные
/// имена содержат <c>#</c>, поэтому пользователь не может их объявить.
/// </summary>
public sealed partial class Binder
{
	private BoundStatement BindMultiAssignment(AssignmentStatement assignment, TupleExpression targets)
	{
		if (assignment.Operator != "=")
		{
			Report(
				"KRN2106",
				$"Составное присваивание '{assignment.Operator}' не поддерживает список целей",
				assignment.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		if (assignment.Value is not TupleExpression values || values.Items.Count != targets.Items.Count)
		{
			Report("KRN2102", "Число целей и значений в присваивании должно совпадать", assignment.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		var statements = new List<BoundStatement>(values.Items.Count * 2);
		var buffer = new List<BoundVariable>(values.Items.Count);
		foreach (var item in values.Items)
		{
			var value = BindExpression(item);
			if (IsError(value.Type))
				return new BoundBlock([], null, FallsThrough: true, assignment.Span);

			var variable = DeclareLocal($"#multi{_nextSlot}", value.Type, false, item.Span);
			DeclareFlow(variable, true, value.Type);
			statements.Add(new BoundLocalDeclaration(
				variable.Name,
				variable.Slot,
				value.Type,
				value,
				false,
				item.Span));
			buffer.Add(variable);
		}

		for (var index = 0; index < targets.Items.Count; index++)
		{
			if (targets.Items[index] is not IdentifierExpression target)
			{
				Report(
					"KRN2106",
					"Целью присваивания может быть только локальная переменная (M3-A)",
					targets.Items[index].Span);
				return new BoundBlock([], null, FallsThrough: true, assignment.Span);
			}

			var variable = _scope.Lookup(target.Name);
			if (variable is null)
			{
				Report("KRN2103", $"Неизвестная переменная '{target.Name}'", target.Span);
				return new BoundBlock([], null, FallsThrough: true, assignment.Span);
			}

			// Запись в захваченную переменную: см. BindAssignment (захват по значению).
			if (variable.Frame != _frame)
			{
				Report(
					"KRN2109",
					$"Запись в захваченную переменную '{variable.Name}' не поддерживается: замыкание хранит копию значения",
					target.Span);
				return new BoundBlock([], null, FallsThrough: true, assignment.Span);
			}

			BoundVariable temporary = buffer[index];
			var value = Convert(
				new BoundVariableExpression(temporary.Name, temporary.Slot, temporary.Type, target.Span),
				variable.Type,
				target.Span);
			if (IsError(value.Type))
				return new BoundBlock([], null, FallsThrough: true, assignment.Span);

			_flow.MarkAssigned(variable.Slot);
			statements.Add(new BoundAssignment(variable.Name, variable.Slot, variable.Type, value, target.Span));
		}

		return new BoundBlock(statements, null, FallsThrough: true, assignment.Span);
	}
}
