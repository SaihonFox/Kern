using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;

namespace Kern.Lib.Transpile.Core;

public abstract partial class Lowerer
{
	private string? PatternCondition(BoundPattern pattern, string subject, List<PatternBinding> bindings)
	{
		switch (pattern)
		{
			case BoundWildcardPattern:
				return null;
			case BoundBindingPattern bindingPattern:
				bindings.Add(new PatternBinding(
					bindingPattern.Name, bindingPattern.Slot, bindingPattern.Type, subject, false));
				return null;
			case BoundLiteralPattern literal:
				return $"{subject} == {StripParens(Value(literal.Value))}";
			case BoundRangePattern range:
			{
				var parts = new List<string>();
				parts.Add($"{subject} >= {StripParens(Value(range.Start))}");
				parts.Add($"{subject} {(range.Inclusive ? "<=" : "<")} {StripParens(Value(range.End))}");
				return "(" + string.Join(" && ", parts) + ")";
			}
			case BoundOrPattern orPattern:
			{
				var parts = new List<string>();
				foreach (BoundPattern alternative in orPattern.Alternatives)
				{
					string? condition = PatternCondition(alternative, subject, bindings);
					parts.Add(condition ?? "true");
				}

				return "(" + string.Join(" || ", parts) + ")";
			}
			case BoundNilPattern:
				return $"{subject} == null";
			case BoundTypePattern typePattern:
			{
				string condition = IsType(subject, typePattern.Type, false);
				if (typePattern.Binding is not null)
					PatternCondition(typePattern.Binding, Cast(subject, typePattern.Type), bindings);
				return condition;
			}
			case BoundListPattern listPattern:
				return ListPatternCondition(listPattern, subject, bindings);
			case BoundConstructorPattern constructor:
				return ConstructorPatternCondition(constructor, subject, bindings);
			case BoundStructuralPattern structural:
				return StructuralPatternCondition(structural, subject, bindings);
			case BoundGuardPattern guard:
				return StripParens(Value(guard.Condition));
			case BoundUnsupportedPattern:
				return null;
			default:
				Warnings.Add($"паттерн {pattern.GetType().Name} не поддержан");
				return null;
		}
	}

	private string? ListPatternCondition(BoundListPattern pattern, string subject, List<PatternBinding> bindings)
	{
		var conditions = new List<string>();
		int count = pattern.Items.Count;
		if (pattern.HasRest)
			conditions.Add($"{LenText(subject)} >= {count}");
		else
			conditions.Add($"{LenText(subject)} == {count}");

		for (var index = 0; index < count; index++)
		{
			BoundPattern item = pattern.Items[index];
			bool afterRest = pattern.HasRest && index >= pattern.ItemsBeforeRest;
			string element = afterRest
				? $"{subject}[{LenText(subject)} - {count - index}]"
				: $"{subject}[{index}]";
			string? condition = PatternCondition(item, element, bindings);
			if (condition is not null)
				conditions.Add(condition);
		}

		if (pattern.HasRest && pattern.Rest is { } rest)
		{
			int tail = count - pattern.ItemsBeforeRest;
			string slice = RestSlice(
				subject,
				pattern.ItemsBeforeRest.ToString(),
				$"{LenText(subject)} - {tail}",
				false);
			bindings.Add(new PatternBinding(rest.Name, rest.Slot, rest.Type, Cast(slice, rest.Type), false));
		}

		return string.Join(" && ", conditions);
	}

	private string? ConstructorPatternCondition(BoundConstructorPattern pattern, string subject, List<PatternBinding> bindings)
	{
		if (pattern.Type is UnitType unit && ResultTypes.IsResultVariant(unit.Symbol))
		{
			bool ok = unit.Symbol.Name == ResultTypes.OkName;
			var conditions = new List<string> { ResultIsOk(subject, ok) };
			if (pattern.Fields.Count > 0)
			{
				string payload = ok ? ResultValue(subject) : ResultError(subject);
				string? inner = PatternCondition(pattern.Fields[0].Pattern, payload, bindings);
				if (inner is not null)
					conditions.Add(inner);
			}

			return string.Join(" && ", conditions);
		}

		var parts = new List<string> { IsType(subject, pattern.Type, false) };
		foreach (BoundFieldPattern field in pattern.Fields)
		{
			string? condition = PatternCondition(
				field.Pattern, $"{subject}.{MemberName(field.Name)}", bindings);
			if (condition is not null)
				parts.Add(condition);
		}

		return string.Join(" && ", parts);
	}

	private string? StructuralPatternCondition(BoundStructuralPattern pattern, string subject, List<PatternBinding> bindings)
	{
		var parts = new List<string>();
		if (pattern.Type is not ClassType and not UnitType)
			parts.Add(IsType(subject, pattern.Type, false));
		foreach (BoundFieldPattern field in pattern.Fields)
		{
			string? condition = PatternCondition(
				field.Pattern, $"{subject}.{MemberName(field.Name)}", bindings);
			if (condition is not null)
				parts.Add(condition);
		}

		return parts.Count == 0 ? null : string.Join(" && ", parts);
	}

	private void EmitBindings(IEnumerable<PatternBinding> bindings)
	{
		foreach (PatternBinding binding in bindings)
		{
			if (binding.Assign)
			{
				W.Line($"{NameOfSlot(binding.Slot, binding.Name)} = {StripParens(binding.Expression)}{End}");
				continue;
			}

			DeclareLocal(NameOfSlot(binding.Slot, binding.Name), binding.Type, StripParens(binding.Expression), true);
		}
	}

	// ------------------------------------------------------------------ match / loop

	private void MatchControl(BoundMatchExpression match, Dest dest)
	{
		string subject = match.Target is null
			? "true"
			: HoistIfNeeded(Value(match.Target), match.Target.Type);
		bool opened = false;
		bool exhaustive = false;
		foreach (BoundMatchArm arm in match.Arms)
		{
			var bindings = new List<PatternBinding>();
			string? condition = match.Target is null ? null : PatternCondition(arm.Pattern, subject, bindings);
			string? guard = arm.Guard is null ? null : StripParens(Value(arm.Guard));
			string? full = (condition, guard) switch
			{
				(null, null) => null,
				({ } c, null) => c,
				(null, { } g) => g,
				({ } c, { } g) => $"{c} && ({g})"
			};

			if (full is null)
			{
				if (opened)
					W.CloseOpen("else");
				else
					W.Open("");
				opened = true;
				EmitBindings(bindings);
				WithResultHint(match.Type, () => Control(arm.Body, dest));
				exhaustive = true;
				break;
			}

			string head = $"if ({full})";
			if (opened)
				W.CloseOpen("else " + head);
			else
				W.Open(head);
			opened = true;
			EmitBindings(bindings);
			WithResultHint(match.Type, () => Control(arm.Body, dest));
		}

		if (!opened)
			return;
		if (!exhaustive && dest.Kind != DestKind.Discard)
		{
			W.CloseOpen("else");
			W.Line(ThrowText("match: ни одна ветвь не совпала"));
		}

		W.Close();
	}

	private string LoopValue(BoundLoopExpression loop)
	{
		string? list = loop.ListSlot >= 0 ? NameOfSlot(loop.ListSlot, "list") : null;
		LoopControl(loop, list is null ? Dest.Discard : Dest.AddTo(list));
		return list ?? NoneLit;
	}

	private void LoopControl(BoundLoopExpression loop, Dest dest)
	{
		string? accumulator = null;
		Dest inner = dest;
		if (dest.Kind is not DestKind.Discard and not DestKind.AddTo)
		{
			accumulator = Temp("acc");
			DeclareLocal(accumulator, loop.Type, NewListText(ListElement(loop.Type)), false);
			inner = Dest.AddTo(accumulator);
		}

		if (loop.IsDoWhile && loop.Condition is not null)
		{
			OpenDoWhileLoop();
			EmitLoopBody(loop, inner);
			CloseDoWhile(StripParens(Value(loop.Condition)));
		}
		else if (loop.Condition is null || loop.IsInfinite)
		{
			OpenInfiniteLoop();
			EmitLoopBody(loop, inner);
			W.Close();
		}
		else
		{
			var (condition, text) = Capture(() => Value(loop.Condition));
			if (text.Length == 0)
			{
				OpenWhile(StripParens(condition));
			}
			else
			{
				OpenInfiniteLoop();
				W.Line(text.TrimEnd('\n'));
				W.Open($"if (!({StripParens(condition)}))");
				W.Line("break" + End);
				W.Close();
			}

			EmitLoopBody(loop, inner);
			W.Close();
		}

		if (accumulator is not null)
			Finish(dest, accumulator, loop);
	}

	private void EmitLoopBody(BoundLoopExpression loop, Dest dest)
	{
		EmitBlock(loop.Body, dest);
		if (loop.Increment is not null)
			Stmt(loop.Increment);
	}

	private static KernType ListElement(KernType type) =>
		type is BuiltinGenericType { Arguments.Count: > 0 } generic ? generic.Arguments[0] : PrimitiveType.Object;

	// ------------------------------------------------------------------ запись по цепи

	private void ChainStore(BoundChainStore store)
	{
		string target = StoreRootText(store.Root);
		foreach (BoundStoreLink link in store.Links)
			target += "." + MemberName(link.MemberName);
		W.Line($"{target} = {StripParens(Value(store.Value))}{End}");
	}

	private string StoreRootText(BoundStoreRoot root) => root switch
	{
		BoundLocalStoreRoot local => NameOfSlot(local.Slot, local.Name),
		BoundSelfStoreRoot => This,
		BoundStaticFieldStoreRoot field => FieldTarget(null, field.Field),
		BoundStaticPropertyStoreRoot property => property.Property.Owner is { } owner
			? $"{TypeName(InstanceTypeOf(owner))}.{MemberName(property.Property.Name)}"
			: MemberName(property.Property.Name),
		BoundExpressionStoreRoot expression => Value(expression.Expression),
		_ => NoneLit
	};
}
