using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Кортежи (spec §3): литерал <c>(10, "hello")</c> и доступ к элементу <c>t.x</c>.
/// Ожидаемый тип у литерала забирает имена элементов (<c>(i32 x, i32 y) point = (3, 4)</c>):
/// собственного именованного синтаксиса литерала spec §3 не описывает.
/// </summary>
public sealed partial class Binder
{
	private BoundExpression BindTupleLiteral(TupleExpression literal, KernType? hint)
	{
		var expected = hint as TupleType;
		if (expected is not null && expected.Elements.Count != literal.Items.Count)
		{
			Report(
				"KRN2102",
				$"У кортежа {expected.Elements.Count} элементов, а в литерале {literal.Items.Count}",
				literal.Span);
			return new BoundErrorExpression(literal.Span);
		}

		var items = new List<BoundExpression>(literal.Items.Count);
		var elements = new List<TupleElement>(literal.Items.Count);
		for (var index = 0; index < literal.Items.Count; index++)
		{
			// Ожидаемый тип задаёт и типы элементов, и их имена: числовое расширение
			// и подстановка имён выполняются так же, как у объявления переменной.
			var elementHint = expected?.Elements[index].Type;
			var item = BindExpression(literal.Items[index], elementHint);
			if (IsError(item.Type))
				return new BoundErrorExpression(literal.Span);

			var type = expected?.Elements[index].Type ?? item.Type;
			var converted = Convert(item, type, literal.Items[index].Span);
			if (IsError(converted.Type))
				return new BoundErrorExpression(literal.Span);

			items.Add(converted);
			elements.Add(new TupleElement(type, expected?.Elements[index].Name));
		}

		return new BoundTupleExpression(items, new TupleType(elements), literal.Span);
	}

	/// <summary>
	/// <c>t.x</c> для кортежа (spec §3): доступ по имени элемента; у безымянного
	/// кортежа имён нет, такая попытка — <c>KRN2103</c>.
	/// </summary>
	private BoundExpression BindTupleItemAccess(
		BoundExpression receiver,
		TupleType tuple,
		MemberAccessExpression member)
	{
		for (var index = 0; index < tuple.Elements.Count; index++)
		{
			if (string.Equals(tuple.Elements[index].Name, member.Member, StringComparison.Ordinal))
				return new BoundTupleItemExpression(receiver, index, tuple.Elements[index].Type, member.Span);
		}

		var names = tuple.Elements
			.Where(element => element.Name is not null)
			.Select(element => element.Name!)
			.ToArray();
		var hint = names.Length == 0
			? "у элементов кортежа нет имён"
			: $"доступны: {string.Join(", ", names)}";
		Report("KRN2103", $"У кортежа '{tuple}' нет элемента '{member.Member}' ({hint})", member.Span);
		return new BoundErrorExpression(member.Span);
	}
}
