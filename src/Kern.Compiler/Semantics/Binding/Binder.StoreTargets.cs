using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Запись через цепь членов (M3-D4, Q13): <c>a.b.c = v</c>, <c>T.cache.x = v</c>,
/// <c>$.inner.x = v</c>. Каждое промежуточное <c>unit</c>-значение читается копией,
/// изменяется и пишется обратно наружу; class-ссылки изменяются на месте. Звено,
/// которое нельзя записать обратно (get-only/<c>init</c>-свойство у unit-значения,
/// <c>unit!</c> в пути), — ошибка, а не потерянная запись.
/// </summary>
public sealed partial class Binder
{
	/// <summary>Цель — цепь длиннее одного звена? Скобки вокруг приёмника не считаются.</summary>
	private static bool IsNestedStoreTarget(MemberAccessExpression member) =>
		UnwrapParens(member.Target) is MemberAccessExpression;

	/// <summary>Снимает скобки, не меняя узел: <c>(a).b</c> и <c>(a.b)</c> — те же места.</summary>
	private static ExpressionNode UnwrapParens(ExpressionNode expression)
	{
		while (expression is ParenthesizedExpression parenthesized)
			expression = parenthesized.Expression;

		return expression;
	}

	/// <summary>
	/// Приёмник однозвенной записи — место (локальная/параметр или поле), а не
	/// временное значение: <c>f().x = 1</c> для <c>unit</c> теряет запись (KRN2106).
	/// </summary>
	private static bool IsPlaceReceiver(BoundExpression? receiver) => receiver switch
	{
		null => true,
		BoundVariableExpression => true,
		BoundFieldExpression => true,
		_ => false,
	};

	/// <summary>
	/// Разбирает цель <c>a.b.c</c> сверху вниз, привязывает корень и звенья и
	/// возвращает один узел записи всей цепи.
	/// </summary>
	private BoundStatement BindChainStore(AssignmentStatement assignment, MemberAccessExpression outer)
	{
		var nodes = new List<MemberAccessExpression>();
		ExpressionNode rootSyntax = outer;
		while (true)
		{
			rootSyntax = UnwrapParens(rootSyntax);
			if (rootSyntax is not MemberAccessExpression access)
				break;

			nodes.Add(access);
			rootSyntax = access.Target;
		}

		nodes.Reverse();

		BoundStoreRoot? root;
		var firstLink = 0;
		if (rootSyntax is IdentifierExpression identifier && TypeReceiverOf(identifier) is { } staticType)
		{
			// `T.cache.x`: сам узел `cache` — корень-место, а не звено.
			root = BindStaticStoreRoot(staticType, nodes[0]);
			firstLink = 1;
		}
		else
		{
			root = BindInstanceStoreRoot(rootSyntax);
		}

		if (root is null)
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);

		if (root.Type is UnitType { IsImmutable: true })
		{
			Report("KRN2105", $"Нельзя изменять значение неизменяемого unit!-типа '{root.Type}'", root.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		var links = new List<BoundStoreLink>(nodes.Count - firstLink);
		var type = root.Type;
		for (var index = firstLink; index < nodes.Count; index++)
		{
			if (index > firstLink && type is UnitType { IsImmutable: true })
			{
				Report(
					"KRN2105",
					$"Нельзя изменять через '{nodes[index - 1].Member}' значение неизменяемого unit!-типа",
					nodes[index - 1].Span);
				return new BoundBlock([], null, FallsThrough: true, assignment.Span);
			}

			var link = BindStoreLink(type, nodes[index], index == nodes.Count - 1, root, index == firstLink);
			if (link is null)
				return new BoundBlock([], null, FallsThrough: true, assignment.Span);

			links.Add(link);
			type = link.Type;
		}

		// Составное присваивание читает текущее значение конечного звена уже в
		// сгенерированной цепи: приёмник не вычисляется второй раз.
		var final = links[^1];
		BoundExpression value;
		if (assignment.Operator == "=")
		{
			value = BindExpression(assignment.Value);
		}
		else
		{
			if (final is BoundPropertyStoreLink { Getter: null })
			{
				Report("KRN2103", $"Свойство '{final.MemberName}' недоступно для чтения", nodes[^1].Span);
				return new BoundBlock([], null, FallsThrough: true, assignment.Span);
			}

			var current = new BoundStoreCurrentExpression(final, final.Type, nodes[^1].Span);
			var right = BindExpression(assignment.Value);
			value = BindBinaryOperator(
				assignment.Operator.TrimEnd('='),
				current,
				right,
				assignment.Span);
			if (IsError(value.Type))
				return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		value = Convert(value, final.Type, assignment.Value.Span);
		return new BoundChainStore(root, links, value, assignment.Span);
	}

	/// <summary>Корень-место, не начинающийся с имени типа.</summary>
	private BoundStoreRoot? BindInstanceStoreRoot(ExpressionNode syntax)
	{
		syntax = UnwrapParens(syntax);
		switch (syntax)
		{
			case SelfExpression self:
				if (_selfVariable is null)
				{
					Report("KRN2106", "Выражение '$' допустимо только в методе или конструкторе типа", self.Span);
					return null;
				}

				return new BoundSelfStoreRoot(_selfVariable.Slot, _selfVariable.Type, self.Span);

			case IdentifierExpression identifier:
				if (_scope.Lookup(identifier.Name) is not null)
				{
					// Повторяет проверку «до присваивания» и smart-cast обычного чтения.
					if (BindIdentifier(identifier) is not BoundVariableExpression variable)
						return null;

					return new BoundLocalStoreRoot(variable.Name, variable.Slot, variable.Type, identifier.Span);
				}

				// Без квалификации внутри члена своего типа: статическое поле (M3-D3b).
				if (UnqualifiedStaticField(identifier.Name, identifier.Span) is { } staticField)
				{
					if (!CheckStaticRootWritable(staticField.Field, identifier.Span))
						return null;

					return new BoundStaticFieldStoreRoot(staticField.Field, staticField.Type, identifier.Span);
				}

				Report("KRN2103", $"Неизвестное имя '{identifier.Name}'", identifier.Span);
				return null;

			default:
				var expression = BindExpression(syntax);
				if (IsError(expression.Type))
					return null;

				if (expression.Type is UnitType { IsImmutable: true })
				{
					Report("KRN2105", $"Нельзя изменять значение неизменяемого unit!-типа '{expression.Type}'", syntax.Span);
					return null;
				}

				if (expression.Type is UnitType)
				{
					// `Point(0, 0).x = 1` / `f().x = 1`: запись ушла бы в копию.
					Report(
						"KRN2106",
						"Нельзя записывать через временное unit-значение: изменение будет потеряно",
						syntax.Span);
					return null;
				}

				return new BoundExpressionStoreRoot(expression, expression.Type, syntax.Span);
		}
	}

	/// <summary>
	/// Корень <c>T.member</c>: статическое поле/автосвойство (место) или
	/// вычисляемое статическое свойство (getter + setter).
	/// </summary>
	private BoundStoreRoot? BindStaticStoreRoot(TypeSymbol type, MemberAccessExpression node)
	{
		var field = type.Members.FindField(node.Member);
		if (field is not null)
		{
			if (!field.IsStatic)
			{
				Report(
					"KRN2103",
					$"Член '{node.Member}' типа '{type.Name}' не статический: нужен экземпляр",
					node.Span);
				return null;
			}

			if (!CheckStaticRootWritable(field, node.Span))
				return null;

			return new BoundStaticFieldStoreRoot(field, field.Type, node.Span);
		}

		var property = type.Members.FindProperty(node.Member);
		if (property is null)
		{
			Report("KRN2103", $"У типа '{type.Name}' нет статического члена '{node.Member}'", node.Span);
			return null;
		}

		if (!property.IsStatic)
		{
			Report(
				"KRN2103",
				$"Член '{node.Member}' типа '{type.Name}' не статический: нужен экземпляр",
				node.Span);
			return null;
		}

		if (property.BackingField is { } backing)
		{
			if (property.Setter is not null)
			{
				if (!CheckStaticRootWritable(backing, node.Span))
					return null;

				return new BoundStaticFieldStoreRoot(backing, property.Type, node.Span);
			}

			if (property.Init is not null)
			{
				Report("KRN2105", InitOnlyMessage(property), node.Span);
				return null;
			}

			if (property.Type is UnitType)
			{
				Report("KRN2105", ReadOnlyMessage(property), node.Span);
				return null;
			}

			// Get-only class-ссылка: читаем backing-поле, запись обратно не нужна.
			return new BoundExpressionStoreRoot(
				new BoundFieldExpression(Receiver: null, backing, property.Type, node.Span),
				property.Type,
				node.Span);
		}

		if (property.Getter is not { } getter)
		{
			Report(
				"KRN2103",
				$"Свойство '{property.Name}' типа '{type.Name}' доступно только для записи",
				node.Span);
			return null;
		}

		if (property.Setter is { } setter)
			return new BoundStaticPropertyStoreRoot(property, getter, setter, property.Type, node.Span);

		if (property.Init is not null)
		{
			Report("KRN2105", InitOnlyMessage(property), node.Span);
			return null;
		}

		if (property.Type is UnitType)
		{
			Report("KRN2105", ReadOnlyMessage(property), node.Span);
			return null;
		}

		return new BoundExpressionStoreRoot(
			new BoundCallExpression(getter, [], property.Type, node.Span, Receiver: null),
			property.Type,
			node.Span);
	}

	/// <summary>Одно звено цепи: поле, автосвойство или вычисляемое свойство.</summary>
	private BoundStoreLink? BindStoreLink(
		KernType receiverType,
		MemberAccessExpression node,
		bool isFinal,
		BoundStoreRoot root,
		bool isFirstLink)
	{
		var owner = OwnerOf(receiverType);
		if (owner is null)
		{
			Report("KRN2103", $"У типа '{receiverType}' нет полей и свойств", node.Span);
			return null;
		}

		var field = owner.Members.FindField(node.Member);
		if (field is not null)
		{
			if (field.IsStatic)
			{
				Report(
					"KRN2103",
					$"Статический член '{field.Name}' нельзя использовать через экземпляр; нужен тип '{owner.Name}'",
					node.Span);
				return null;
			}

			return new BoundFieldStoreLink(field, field.Type, node.Span);
		}

		var property = owner.Members.FindProperty(node.Member);
		if (property is not null)
		{
			if (property.IsStatic)
			{
				Report(
					"KRN2103",
					$"Статический член '{property.Name}' нельзя использовать через экземпляр; нужен тип '{owner.Name}'",
					node.Span);
				return null;
			}

			return BindPropertyStoreLink(property, node, isFinal, root, isFirstLink);
		}

		Report("KRN2103", $"Неизвестный член '{node.Member}' у типа '{receiverType}'", node.Span);
		return null;
	}

	private BoundStoreLink? BindPropertyStoreLink(
		PropertySymbol property,
		MemberAccessExpression node,
		bool isFinal,
		BoundStoreRoot root,
		bool isFirstLink)
	{
		// init пишется только у `$` и только при создании этого объекта (Q15b).
		var initAllowed = isFirstLink &&
			root is BoundSelfStoreRoot &&
			ReferenceEquals(_constructionType, property.Owner);

		if (property.BackingField is { } backing)
		{
			// Автосвойство: запись идёт в backing-поле, как у однозвенной цели.
			if (property.Setter is not null)
				return new BoundFieldStoreLink(backing, property.Type, node.Span);

			if (property.Init is not null)
			{
				if (initAllowed)
					return new BoundFieldStoreLink(backing, property.Type, node.Span);

				Report("KRN2105", InitOnlyMessage(property), node.Span);
				return null;
			}

			// Промежуточное class-звено не пишется обратно, поэтому get-only
			// свойство годится; для unit запись обратно обязательна.
			if (isFinal || property.Type is UnitType)
			{
				Report("KRN2105", ReadOnlyMessage(property), node.Span);
				return null;
			}

			return new BoundFieldStoreLink(backing, property.Type, node.Span);
		}

		if (property.Getter is not { } getter)
		{
			// Конечному звену читать нечего: достаточно setter'а (как у однозвенной
			// цели). Промежуточное set-only звено пришлось бы читать, чтобы идти дальше.
			if (isFinal && property.Setter is { } setOnly)
				return new BoundPropertyStoreLink(property, Getter: null, Writer: setOnly, property.Type, node.Span);

			Report("KRN2103", $"Свойство '{property.Name}' доступно только для записи", node.Span);
			return null;
		}

		if (property.Setter is { } setter)
			return new BoundPropertyStoreLink(property, getter, setter, property.Type, node.Span);

		if (property.Init is { } init)
		{
			if (initAllowed)
				return new BoundPropertyStoreLink(property, getter, init, property.Type, node.Span);

			Report("KRN2105", InitOnlyMessage(property), node.Span);
			return null;
		}

		if (isFinal || property.Type is UnitType)
		{
			Report("KRN2105", ReadOnlyMessage(property), node.Span);
			return null;
		}

		return new BoundPropertyStoreLink(property, getter, Writer: null, property.Type, node.Span);
	}

	/// <summary>Запись <c>!</c>-статики разрешена только её собственному статическому инициализатору.</summary>
	private bool CheckStaticRootWritable(FieldSymbol field, TextSpan span)
	{
		if (field.IsRequired && !ReferenceEquals(_staticInitializerType, field.Owner))
		{
			Report(
				"KRN2105",
				$"Статическая константа '{field.Name}' не может быть изменена в обычном коде",
				span);
			return false;
		}

		return true;
	}

	private static string ReadOnlyMessage(PropertySymbol property) =>
		$"Свойство '{property.Name}' доступно только для чтения: цепь записи не может вернуть в него значение";

	private static string InitOnlyMessage(PropertySymbol property) =>
		$"Свойство '{property.Name}' с 'init' можно записать только при создании объекта";
}
