using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Статические поля (M3-D3b, Q15a1/Q15a2): доступ через имя типа (<c>T.field</c>) и без
/// квалификации внутри членов своего типа, когда локального имени нет. Статическое поле
/// живёт в модульном <c>FieldPool</c>, поэтому приёмник-экземпляр у него не эмитится.
/// Привязка статических инициализаторов — <c>BindStaticInitializer</c> в
/// <c>Binder.Initializers.cs</c>; копирование <c>unit</c> (Q13) сюда не входит — это M3-D4.
/// </summary>
public sealed partial class Binder
{
	/// <summary>
	/// Тип в позиции приёмника <c>T.member</c>. Локальная переменная/параметр с тем же
	/// именем важнее: <c>_scope.Lookup</c> проверяется первым.
	/// </summary>
	private TypeSymbol? TypeReceiverOf(ExpressionNode target) =>
		target is IdentifierExpression identifier &&
		_scope.Lookup(identifier.Name) is null &&
		VisibleType(identifier.Name) is { } type
			? type
			: null;

	/// <summary>
	/// Вызов статического метода через имя типа: <c>T.method(...)</c> (spec §8: <c>#fun</c> —
	/// это <c>static</c>). Приёмник не эмитится, поэтому вызов идёт тем же путём, что и
	/// модульная функция. Экземплярные методы сюда не попадают: <c>T.instanceMethod()</c> —
	/// неверное направление, как и у полей.
	/// </summary>
	private BoundExpression BindTypeQualifiedCall(
		TypeSymbol type,
		MemberAccessExpression member,
		IReadOnlyList<CallArgumentNode> arguments,
		IReadOnlyList<TypeSyntax> typeArguments,
		TextSpan span)
	{
		var declared = type.Members.GetFunctions(member.Member);
		var staticMethods = declared.Where(function => function.IsStatic).ToList();
		if (staticMethods.Count == 0)
		{
			if (declared.Count > 0)
				Report(
					"KRN2103",
					$"Метод '{member.Member}' типа '{type.Name}' не статический: нужен экземпляр",
					member.Span);
			else
				Report("KRN2103", $"У типа '{type.Name}' нет статического метода '{member.Member}'", member.Span);
			return new BoundErrorExpression(span);
		}

		return BindTargetlessCall(staticMethods, member.Member, arguments, typeArguments, span);
	}

	/// <summary>
	/// Статическое поле текущего типа по имени без квалификации. Действует только внутри
	/// члена типа и только если локальное имя не нашлось (затенение — как у значений).
	/// </summary>
	private BoundFieldExpression? UnqualifiedStaticField(string name, TextSpan span)
	{
		if (_current?.Owner is not { } owner)
			return null;

		var field = owner.Members.FindField(name);
		return field is { IsStatic: true }
			? new BoundFieldExpression(Receiver: null, field, field.Type, span)
			: null;
	}

	/// <summary>Чтение <c>T.field</c> / <c>T.property</c> через имя типа.</summary>
	private BoundExpression BindStaticMemberAccess(TypeSymbol type, MemberAccessExpression member)
	{
		var field = type.Members.FindField(member.Member);
		if (field is not null)
		{
			if (!field.IsStatic)
				return InstanceMemberThroughType(type, member);

			return new BoundFieldExpression(Receiver: null, field, field.Type, member.Span);
		}

		var property = type.Members.FindProperty(member.Member);
		if (property is null)
		{
			Report("KRN2103", $"У типа '{type.Name}' нет статического члена '{member.Member}'", member.Span);
			return new BoundErrorExpression(member.Span);
		}

		if (!property.IsStatic)
			return InstanceMemberThroughType(type, member);

		if (property.BackingField is { } backing)
			return new BoundFieldExpression(Receiver: null, backing, property.Type, member.Span);

		if (property.Getter is { } getter)
			return new BoundCallExpression(getter, [], property.Type, member.Span, Receiver: null);

		Report("KRN2103", $"Свойство '{member.Member}' типа '{type.Name}' доступно только для записи", member.Span);
		return new BoundErrorExpression(member.Span);
	}

	/// <summary>Запись <c>T.field = v</c> / <c>T.property = v</c> через имя типа.</summary>
	private BoundStatement BindStaticMemberAssignment(
		AssignmentStatement assignment,
		TypeSymbol type,
		MemberAccessExpression member)
	{
		var field = type.Members.FindField(member.Member);
		if (field is not null)
		{
			if (!field.IsStatic)
				return InstanceMemberThroughTypeStatement(type, member, assignment.Span);

			return BindStaticStore(assignment, field, field.Type, member.Span);
		}

		var property = type.Members.FindProperty(member.Member);
		if (property is null)
		{
			Report("KRN2103", $"У типа '{type.Name}' нет статического члена '{member.Member}'", member.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		if (!property.IsStatic)
			return InstanceMemberThroughTypeStatement(type, member, assignment.Span);

		if (property.BackingField is { } backing)
		{
			// Статического «создания объекта» нет, поэтому init-запись запрещена везде,
			// включая статический инициализатор этого типа (Q15b).
			if (InitOnlyPropertyOf(type, backing) is { } initProperty)
			{
				Report(
					"KRN2105",
					$"Свойство '{initProperty.Name}' с 'init' можно записать только при создании объекта",
					assignment.Span);
				return new BoundBlock([], null, FallsThrough: true, assignment.Span);
			}

			return BindStaticStore(assignment, backing, property.Type, member.Span);
		}

		if (property.Setter is not { } setter || assignment.Operator != "=")
		{
			Report(
				"KRN2105",
				$"Свойство '{property.Name}' типа '{type.Name}' нельзя изменить: нужен 'set'",
				assignment.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		var value = Convert(BindExpression(assignment.Value), property.Type, assignment.Value.Span);
		var call = new BoundCallExpression(setter, [value], property.Type, assignment.Span, Receiver: null);
		return new BoundExpressionStatement(call, assignment.Span);
	}

	/// <summary>
	/// Запись статического поля. <c>!</c> у статики — константа: обычный код её не меняет
	/// (<c>KRN2105</c>). Единственное исключение — статический инициализатор типа, который
	/// эту константу объявил (Q15b). Сравнивается объявивший тип поля (<see cref="FieldSymbol.Owner"/>),
	/// а не тип, через который к полю обратились: иначе инициализатор наследника смог бы
	/// записать унаследованную константу родителя.
	/// </summary>
	private BoundStatement BindStaticStore(
		AssignmentStatement assignment,
		FieldSymbol field,
		KernType type,
		TextSpan targetSpan)
	{
		if (field.IsRequired && !ReferenceEquals(_staticInitializerType, field.Owner))
		{
			Report(
				"KRN2105",
				$"Статическая константа '{field.Name}' не может быть изменена в обычном коде",
				assignment.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		var value = BindExpression(assignment.Value);
		if (assignment.Operator != "=")
		{
			// Составное присваивание читает текущее значение статики тем же операндом.
			var current = new BoundFieldExpression(Receiver: null, field, type, targetSpan);
			value = BindBinaryOperator(assignment.Operator.TrimEnd('='), current, value, assignment.Span);
		}

		value = Convert(value, type, assignment.Value.Span);
		return new BoundFieldAssignment(Receiver: null, field, type, value, assignment.Span);
	}

	/// <summary>Экземплярный член вызван через имя типа: <c>T.instanceField</c>.</summary>
	private BoundExpression InstanceMemberThroughType(TypeSymbol type, MemberAccessExpression member)
	{
		Report(
			"KRN2103",
			$"Член '{member.Member}' типа '{type.Name}' не статический: нужен экземпляр",
			member.Span);
		return new BoundErrorExpression(member.Span);
	}

	/// <summary>То же для записи: <c>T.instanceField = v</c>.</summary>
	private BoundStatement InstanceMemberThroughTypeStatement(
		TypeSymbol type,
		MemberAccessExpression member,
		TextSpan span)
	{
		Report(
			"KRN2103",
			$"Член '{member.Member}' типа '{type.Name}' не статический: нужен экземпляр",
			span);
		return new BoundBlock([], null, FallsThrough: true, span);
	}

	/// <summary>Статический член прочитан через экземпляр: <c>obj.field</c>, <c>$field</c>.</summary>
	private BoundExpression StaticMemberThroughInstance(TypeSymbol owner, MemberAccessExpression member)
	{
		Report(
			"KRN2103",
			$"Статический член '{member.Member}' нельзя использовать через экземпляр; нужен тип '{owner.Name}'",
			member.Span);
		return new BoundErrorExpression(member.Span);
	}
}
