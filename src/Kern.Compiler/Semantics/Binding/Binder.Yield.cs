using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>Генераторы: <c>yield</c> и распознавание результата <c>seq&lt;T&gt;</c> (spec §6).</summary>
public sealed partial class Binder
{
	/// <summary>
	/// <c>yield value</c> в генераторе (spec §6): значение проверяется против <c>T</c>
	/// из <c>seq&lt;T&gt;</c>. Вне генератора — ошибка; остаток блока остаётся достижимым.
	/// </summary>
	private BoundStatement BindYield(YieldStatement statement)
	{
		if (!_isGeneratorFunction || _generatorElement is null)
		{
			Report("KRN2101", "yield допустим только в функции с результатом seq<T>", statement.Span);
			BoundExpression? bad = statement.Value is null ? null : BindExpression(statement.Value);
			return new BoundYieldStatement(bad, statement.Span);
		}

		if (statement.Value is null)
		{
			Report("KRN2101", "yield требует значения", statement.Span);
			return new BoundYieldStatement(null, statement.Span);
		}

		var value = Convert(
			BindExpression(statement.Value, _generatorElement),
			_generatorElement,
			statement.Value.Span);
		return new BoundYieldStatement(value, statement.Span);
	}

	/// <summary>Тип элемента, если тип — <c>seq&lt;T&gt;</c>.</summary>
	private static bool TryGetSeqElement(KernType type, out KernType? element)
	{
		if (type is ClassType { Symbol.Name: "seq" } sequence && sequence.Arguments.Count == 1)
		{
			element = sequence.Arguments[0];
			return true;
		}

		element = null;
		return false;
	}
}
