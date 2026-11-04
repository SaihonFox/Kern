using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>
	/// <c>x:{ a = 1 }</c> (spec round 27): копия unit/type с заменёнными полями. Поля ищутся
	/// в объединённой таблице (наследованные тоже), тип поля подставляется аргументами получателя.
	/// <c>unit!</c> отклоняется: его поля неизменяемы и после клонирования.
	/// </summary>
	private BoundExpression BindWith(WithExpression expression)
	{
		BoundExpression target = BindExpression(expression.Target);
		if (target.Type is ErrorType)
			return new BoundErrorExpression(expression.Span);

		if (target.Type is UnitType { IsImmutable: true })
		{
			Report("KRN2101", "with нельзя применять к unit!: значение неизменяемо", expression.Target.Span);
			return new BoundErrorExpression(expression.Span);
		}

		TypeSymbol? owner = OwnerOf(target.Type);
		if (owner is null)
		{
			Report(
				"KRN2101",
				$"with требует unit или type, получено '{target.Type}'",
				expression.Target.Span);
			return new BoundErrorExpression(expression.Span);
		}

		var arguments = TypeArgumentsMap(target.Type);
		var assignments = new List<BoundWithAssignment>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (WithAssignmentNode assignment in expression.Assignments)
		{
			if (!seen.Add(assignment.Name))
			{
				Report(
					"KRN2106",
					$"поле '{assignment.Name}' указано в with несколько раз",
					assignment.Span);
				continue;
			}

			FieldSymbol? field = owner.Members.FindField(assignment.Name);
			if (field is null)
			{
				PropertySymbol? property = owner.Members.FindProperty(assignment.Name);
				if (property is null)
				{
					Report(
						"KRN2101",
						$"у типа '{owner.Name}' нет поля '{assignment.Name}'",
						assignment.Span);
					continue;
				}

				if (property.IsStatic)
				{
					Report(
						"KRN2101",
						$"свойство '{assignment.Name}' статическое: with заменяет только члены экземпляра",
						assignment.Span);
					continue;
				}

				if (!property.CanWrite)
				{
					Report(
						"KRN2105",
						$"свойство '{assignment.Name}' только для чтения: в with нужен set",
						assignment.Span);
					continue;
				}

				// Авто-свойство пишется в backing-поле, как и обычное присваивание;
				// вычисляемое свойство — вызовом setter'а.
				if (property.BackingField is { } backing)
				{
					KernType backingType = Substitute(backing.Type, arguments);
					BoundExpression backingValue = BindExpression(assignment.Value, backingType);
					if (backingValue.Type is ErrorType)
						continue;
					assignments.Add(new BoundWithAssignment(
						backing,
						null,
						Convert(backingValue, backingType, assignment.Value.Span)));
					continue;
				}

				KernType propertyType = Substitute(property.Type, arguments);
				BoundExpression propertyValue = BindExpression(assignment.Value, propertyType);
				if (propertyValue.Type is ErrorType)
					continue;
				assignments.Add(new BoundWithAssignment(
					null,
					property,
					Convert(propertyValue, propertyType, assignment.Value.Span)));
				continue;
			}

			if (field.IsStatic)
			{
				Report(
					"KRN2101",
					$"поле '{assignment.Name}' статическое: with заменяет только поля экземпляра",
					assignment.Span);
				continue;
			}

			KernType fieldType = Substitute(field.Type, arguments);
			BoundExpression value = BindExpression(assignment.Value, fieldType);
			if (value.Type is ErrorType)
				continue;
			assignments.Add(new BoundWithAssignment(
				field,
				null,
				Convert(value, fieldType, assignment.Value.Span)));
		}

		return new BoundWithExpression(target, assignments, expression.Span);
	}
}
