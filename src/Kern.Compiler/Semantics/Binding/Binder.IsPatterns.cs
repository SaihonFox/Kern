using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>
	/// <c>x is Pattern</c> (spec round 27): паттерн связывается существующим
	/// <see cref="BindPattern"/>, его привязки объявляются в текущей области (видны справа
	/// от <c>&amp;&amp;</c> и в then-ветви <c>if</c>). Отрицание с привязками запрещено.
	/// </summary>
	private BoundExpression BindIsPattern(IsPatternExpression expression)
	{
		BoundExpression subject = BindExpression(expression.Subject);
		if (subject.Type is ErrorType)
			return new BoundErrorExpression(expression.Span);

		var bindings = new List<BoundPatternBinding>();
		BoundPattern pattern = BindPattern(expression.Pattern, expression.Negated ? null : subject.Type, bindings);
		if (expression.Negated && bindings.Count > 0)
		{
			Report(
				"KRN2101",
				"Связывания is-паттерна недоступны после отрицания '!is'",
				expression.Span);
		}

		return new BoundIsPatternExpression(subject, pattern, expression.Negated, bindings, expression.Span);
	}
}
