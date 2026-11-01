using Kern.Compiler.Semantics.Binding.Flow;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>
	/// M3-E: match как выражение. Субъект связывается один раз, каждая ветвь получает
	/// собственную область видимости; паттерны, guard и тело видят связывания этой ветви.
	/// </summary>
	private BoundExpression BindMatchExpression(MatchExpression expression)
	{
		BoundExpression? target = null;
		if (!expression.IsTargetless)
			target = BindExpression(expression.Target);

		KernType? subject = target?.Type;
		var entry = _flow.Clone();
		var arms = new List<BoundMatchArm>(expression.Arms.Count);
		var armStates = new List<FlowState>(expression.Arms.Count);
		// После неохраняемой ветви `nil` остальные ветви видят не-nil тип (smart cast
		// `T?` → `T`, spec §4): `match r.find_match(...) { nil => …  m => m.text }`.
		var nilHandled = false;
		foreach (var arm in expression.Arms)
		{
			_flow = entry.Clone();
			KernType? armSubject = nilHandled && subject is NullableType nullable ? nullable.Underlying : subject;
			arms.Add(BindMatchArm(arm, armSubject));
			armStates.Add(_flow);
			if (arm.Guard is null && IsNilPattern(arm.Pattern))
				nilHandled = true;
		}

		_flow = armStates.Count == 0 ? entry : FlowState.Join(armStates);

		// Q12: покрытие и недостижимые ветви — только диагностики, bound-дерево не меняется.
		AnalyzeMatch(target, arms, expression.Span);

		var type = MatchResultType(arms, expression.Span);
		var converted = new List<BoundMatchArm>(arms.Count);
		foreach (var arm in arms)
			converted.Add(arm with { Body = Convert(arm.Body, type, arm.Body.Span) });

		return new BoundMatchExpression(target, converted, type, expression.Span);
	}

	private static bool IsNilPattern(PatternNode pattern) => pattern switch
	{
		IdentifierPattern identifier => identifier.Name == "nil",
		LiteralPattern literal => literal.Text == "nil",
		_ => false,
	};

	private BoundMatchArm BindMatchArm(MatchArm arm, KernType? subject)
	{
		var previous = _scope;
		_scope = new BoundScope(previous);
		try
		{
			var bindings = new List<BoundPatternBinding>();
			var pattern = BindPattern(arm.Pattern, subject, bindings);
			var guard = arm.Guard is null ? null : BindMatchGuard(arm.Guard);
			if (guard is not null)
				ApplyConditionFacts(_flow, guard, whenTrue: true);
			var body = BindBranch(arm.Body);
			return new BoundMatchArm(pattern, guard, body, bindings, arm.Span);
		}
		finally
		{
			_scope = previous;
		}
	}

	private BoundExpression BindMatchGuard(ExpressionNode guard) =>
		BindCondition(guard, "Условие ветви match");

	/// <summary>Общий тип тел ветвей; <c>never</c> — нейтральный элемент.</summary>
	private KernType MatchResultType(IReadOnlyList<BoundMatchArm> arms, TextSpan span)
	{
		KernType? result = null;
		foreach (var arm in arms)
		{
			result = result is null
				? arm.Body.Type
				: CommonExpressionType(result, arm.Body.Type, arm.Span, context: "match");
		}

		return result ?? PrimitiveType.Void;
	}
}
