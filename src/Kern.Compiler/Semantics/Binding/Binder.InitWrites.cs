using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Запись <c>init</c>-членов (Q15b, spec §8): разрешена только у <c>$</c> и только пока
/// объект создаётся — в инициализаторах полей/свойств, прологе и теле конструктора
/// этого типа (включая вложенные блоки, <c>if</c>/<c>match</c>/<c>loop</c>).
/// Вложенные функции (лямбды, локальные функции) контекст не наследуют: их тела
/// привязываются отдельными вызовами <see cref="BindFunction"/>.
/// </summary>
public sealed partial class Binder
{
	/// <summary>init-свойство с телом accessor'а: запись становится вызовом accessor'а.</summary>
	private BoundStatement BindInitAccessorAssignment(
		AssignmentStatement assignment,
		BoundExpression receiver,
		PropertySymbol property,
		FunctionSymbol accessor)
	{
		var value = BindExpression(assignment.Value);
		value = Convert(value, property.Type, assignment.Value.Span);
		if (!CheckInitWrite(receiver, property, assignment.Span))
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);

		var call = new BoundCallExpression(accessor, [value], property.Type, assignment.Span, receiver);
		return new BoundExpressionStatement(call, assignment.Span);
	}

	/// <summary>
	/// init-свойство-автосвойство: запись идёт в backing-поле, как у обычного поля.
	/// Составное присваивание (<c>$.p += 1</c>) тоже проверяется: оно меняет то же поле.
	/// </summary>
	private BoundStatement BindInitFieldAssignment(
		AssignmentStatement assignment,
		BoundFieldExpression field,
		PropertySymbol property)
	{
		var value = BindExpression(assignment.Value);
		if (assignment.Operator != "=")
		{
			var symbol = assignment.Operator.TrimEnd('=');
			value = BindBinaryOperator(symbol, field, value, assignment.Span);
		}

		value = Convert(value, field.Type, assignment.Value.Span);
		if (!CheckInitWrite(field.Receiver, property, assignment.Span))
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);

		return new BoundFieldAssignment(field.Receiver, field.Field, field.Type, value, assignment.Span);
	}

	/// <summary>
	/// Правила записи init-члена (Q15b): только <c>$</c> и только при создании объекта
	/// этого типа. Нарушение — существующий код KRN2105 («присваивание недопустимо»),
	/// как у single-assignment. Неизменяемость <c>unit!</c> здесь не проверяется: она
	/// запрещает изменение ПОСЛЕ создания (spec §9), а init — часть создания (spec §8).
	/// </summary>
	private bool CheckInitWrite(BoundExpression? receiver, PropertySymbol property, TextSpan span)
	{
		// unit! неизменяем только ПОСЛЕ создания (spec §9), а init — это инициализация
		// во время создания (spec §8). Поэтому запись init в конструкторе unit! допустима:
		// её, как и любую init-запись, ограничивает проверка контекста создания ниже.
		// Запись через set у unit! по-прежнему запрещена отдельно (TryBindPropertyAssignment),
		// потому что set — это путь изменения уже созданного объекта.

		// Другой экземпляр: построение чужого объекта этим конструктором не начинается.
		if (!IsSelfReceiver(receiver))
		{
			Report(
				"KRN2105",
				$"Свойство '{property.Name}' с 'init' можно записать только у '$' при создании объекта",
				span);
			return false;
		}

		// Контекст создания: конструктор (пролог/тело) или инициализаторы этого типа.
		if (property.Owner is not { } owner || !ReferenceEquals(_constructionType, owner))
		{
			Report(
				"KRN2105",
				$"Свойство '{property.Name}' с 'init' можно записать только при создании объекта: в инициализаторе, прологе или теле конструктора",
				span);
			return false;
		}

		return true;
	}

	/// <summary><c>$</c> в текущем методе; <c>null</c> — неявный <c>$</c> в уже связанном поле.</summary>
	private bool IsSelfReceiver(BoundExpression? receiver) =>
		receiver is null ||
		(receiver is BoundVariableExpression variable &&
			_selfVariable is { } self &&
			variable.Slot == self.Slot);

	/// <summary>
	/// init-свойство, которому принадлежит backing-поле, если у свойства нет <c>set</c>.
	/// Имя поля скрытое (<c>_p</c>), поэтому ищем по ссылке, а не по имени.
	/// </summary>
	private static PropertySymbol? InitOnlyPropertyOf(TypeSymbol? owner, FieldSymbol field)
	{
		if (owner is null || !field.IsBackingField)
			return null;

		foreach (var property in owner.Members.Properties)
		{
			if (property.Setter is null &&
				property.Init is not null &&
				ReferenceEquals(property.BackingField, field))
				return property;
		}

		return null;
	}
}
