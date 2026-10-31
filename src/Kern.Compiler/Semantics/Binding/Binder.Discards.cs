using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>
	/// <c>_ = expr</c> — явный отказ от значения: выражение вычисляется ради побочных
	/// эффектов, результат отбрасывается без KRN2305. <c>_</c> не объявляет переменную,
	/// поэтому прочитать её нельзя, а для неё допустим только оператор <c>=</c>.
	/// </summary>
	private BoundStatement BindDiscard(AssignmentStatement assignment, IdentifierExpression target)
	{
		if (assignment.Operator != "=")
		{
			Report(
				"KRN2106",
				$"С '{target.Name}' допустим только оператор '='; значение можно только отбросить",
				assignment.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		var value = BindExpression(assignment.Value);
		if (_flow.Reachable && !ExpressionFallsThrough(value))
			_flow.MarkUnreachable();
		return new BoundDiscardStatement(value, assignment.Span);
	}

	/// <summary>
	/// KRN2305: значение типа <c>result&lt;T, E&gt;</c> или <c>task&lt;T&gt;</c> потеряно
	/// в позиции оператора. Тип проверяется по типизированному дереву, а не по тексту.
	/// С B7 то же предупреждение получает значение, помеченное <c>#[must_use]</c> (функция
	/// или тип): на одно потерянное выражение выдаётся не больше одной диагностики — проверка
	/// <c>result</c>/<c>task</c> идёт первой.
	/// <c>if</c> без <c>else</c> всегда имеет тип <c>void</c> и отбрасывает значение ветки
	/// <c>then</c>, поэтому проверяем её отдельно — иначе вложенный <c>if cond { f() }</c>
	/// не предупредил бы.
	/// </summary>
	private void WarnDiscardedValue(BoundExpression expression, TextSpan span)
	{
		if (expression is BoundIfExpression { Else: null } conditional)
		{
			WarnDiscardedValue(conditional.Then, span);
			return;
		}

		// Блок в позиции инструкции отбрасывает своё хвостовое значение — смотрим на него.
		if (expression is BoundBlockExpression { Value: { } value })
		{
			WarnDiscardedValue(value, span);
			return;
		}

		if (ResultTypes.IsResultType(expression.Type) || expression.Type is BuiltinGenericType
			{ BuiltinKind: BuiltinGenericKind.Task } type)
		{
			Warn("KRN2305", $"Результат типа '{expression.Type}' отброшен; используйте `_ =` или обработайте его", span);
			return;
		}

		if (IsMustUse(expression))
			Warn("KRN2305", $"Значение типа '{expression.Type}' помечено `#[must_use]` и отброшено; используйте `_ =`", span);
	}
}
