using Kern.Bytecode;
using Kern.Compiler.Semantics.Binding.Flow;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Bound.Expressions;
using Kern.Compiler.Semantics.Bound.Patterns;
using Kern.Compiler.Semantics.Symbols;
using Kern.Compiler.Semantics.Types;
using Kern.Core;
using Syn = Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding.Core;

public sealed partial class Binder
{
	/// <summary>`nil` в образце: литерал мог быть обёрнут приведением к `T?`, поэтому смотрим сквозь BoundConversion.</summary>
	private static bool IsNilValue(BoundExpr e) => e is BoundNilLiteral || (e is BoundConversion { Operand: BoundNilLiteral });

	private static bool IsNilPattern(BoundPattern p) => p is BoundValuePattern v && IsNilValue(v.Value);

	private BoundExpr BindMatch(Syn.MatchExpr m, TypeSymbol? expected)
	{
		BoundExpr? target = m.Target is null ? null : Bind(m.Target);
		var subject = target?.Type ?? PrimitiveType.Bool;

		var pre = Flow.Fork();
		var endStates = new List<FlowState>();
		var arms = new List<BoundMatchArm>();
		bool catchAll = false;
		bool reportedUnreachable = false;
		bool nilCovered = false;   // предыдущая ветка без guard уже забрала nil: дальше значение точно не nil

		foreach (var arm in m.Arms)
		{
			if (catchAll && !reportedUnreachable)
			{
				Warn(Krn.UnreachableArm, "ветка недостижима: предыдущая ветка совпадает с любым значением", arm.Span);
				reportedUnreachable = true;
			}

			PushScope("match arm");
			var armState = pre.Clone();
			Flow.Current = armState;
			var bindings = new List<LocalSymbol>();
			BoundPattern pattern;

			if (target is null)
			{
				if (arm.Pattern is Syn.ValuePattern vp)
				{
					var cond = BindCondition(vp.Value, "условие ветки `match`");
					pattern = new BoundValuePattern(cond, null, PrimitiveType.Bool, vp.Span);
					ApplyCondition(cond, Flow.Current, null);
				}
				else if (arm.Pattern is Syn.WildcardPattern wp)
					pattern = new BoundWildcardPattern(PrimitiveType.Bool, wp.Span);
				else
				{
					Error(Krn.InvalidPattern, "в `match` без значения допустимы только условия и `_`", arm.Pattern.Span);
					pattern = new BoundWildcardPattern(PrimitiveType.Bool, arm.Pattern.Span);
				}
			}
			else
			{
				var armSubject = nilCovered && subject is NullableType ns ? ns.Inner : subject;
				pattern = BindPattern(arm.Pattern, armSubject, bindings, false);
				NarrowByPattern(target, pattern);
			}

			BoundExpr? guard = null;
			if (arm.Guard is not null)
			{
				guard = BindCondition(arm.Guard, "условие `if` ветки");
				ApplyCondition(guard, Flow.Current, null);
			}

			var body = Bind(arm.Body, expected);
			endStates.Add(Flow.Current);
			PopScope();
			arms.Add(new BoundMatchArm(pattern, guard, body, bindings, arm.Span));

			if (guard is null && IsNilPattern(pattern)) nilCovered = true;
			if (guard is null && pattern is BoundWildcardPattern or BoundBindingPattern) catchAll = true;
			if (guard is null && pattern is BoundTypePattern tp0 && target is not null && tp0.TestType.Equals(subject)) catchAll = true;
		}

		bool exhaustive = catchAll || (target is not null && IsExhaustive(subject, arms));
		if (!exhaustive && m.Arms.Count > 0)
			Warn(Krn.NonExhaustiveMatch, target is null
				? "`match` без `_` может не сработать ни для одного условия"
				: $"`match` может не покрывать все значения типа '{subject.ToDisplayString()}' — добавьте ветку `_`", m.Span);

		var states = new List<FlowState>(endStates);
		if (!exhaustive || arms.Count == 0) states.Add(pre);
		Flow.Current = FlowState.Join(states);

		// общий тип результата
		TypeSymbol? type = null;
		foreach (var a in arms)
		{
			var t = a.Body.Type;
			type = type is null ? t : UnifyBranchTypes(type, t, expected, a.Span);
			if (type.IsError) break;
		}
		type ??= PrimitiveType.Void;

		if (!type.IsError && !IsVoid(type) && type is not NeverType)
			arms = [.. arms.Select(a => a.Body.Type is NeverType ? a : a with { Body = Convert(a.Body, type, a.Span) })];

		return new BoundMatch(target, arms, exhaustive, type, m.Span);
	}

	private void NarrowByPattern(BoundExpr target, BoundPattern pattern)
	{
		switch (pattern)
		{
			case BoundTypePattern tp:
				Flow.ApplyIs(Flow.Current, target, tp.TestType, positive: true);
				break;
			case BoundDestructurePattern { Kind: DestructureKind.Object } dp when !dp.TestType.IsError:
				Flow.ApplyIs(Flow.Current, target, dp.TestType, positive: true);
				break;
			case BoundValuePattern v0 when IsNilValue(v0.Value):
				break;
			case BoundValuePattern { Value: not BoundRange } when target.Type is NullableType:
				Flow.ApplyHasValue(Flow.Current, target);
				break;
		}
	}

	// ------------------------------------------------------------ полнота

	private static bool IsExhaustive(TypeSymbol subject, IReadOnlyList<BoundMatchArm> arms)
	{
		var unguarded = arms.Where(a => a.Guard is null).Select(a => a.Pattern).ToList();

		if (subject is NullableType n)
		{
			bool hasNil = unguarded.Any(IsNilPattern);
			return hasNil && IsExhaustive(n.Inner, arms);
		}

		switch (subject)
		{
			case PrimitiveType { Kind: PrimKind.Bool }:
			{
				var values = unguarded.OfType<BoundValuePattern>().Select(p => p.Value).OfType<BoundBoolLiteral>().Select(b => b.Value).ToHashSet();
				return values.Contains(true) && values.Contains(false);
			}
			case EnumType e:
			{
				var covered = unguarded.OfType<BoundValuePattern>()
					.Select(p => (p.Value as BoundField)?.Field).Where(f => f is { IsEnumEntry: true }).ToHashSet();
				return e.Decl.EnumEntries.All(covered.Contains);
			}
			case UnionType u:
				return u.Members.All(m => unguarded.Any(p =>
					(p is BoundTypePattern tp && tp.TestType.Equals(m)) || (p is BoundDestructurePattern { Kind: DestructureKind.Object } dp && dp.TestType.Equals(m))));
			case ResultType:
				return unguarded.OfType<BoundDestructurePattern>().Any(p => p.Kind == DestructureKind.ResultOk && IsIrrefutableElements(p))
					&& unguarded.OfType<BoundDestructurePattern>().Any(p => p.Kind == DestructureKind.ResultErr && IsIrrefutableElements(p));
		}
		return false;
	}

	private static bool IsIrrefutableElements(BoundDestructurePattern p) =>
		p.Elements.All(e => e is BoundWildcardPattern or BoundBindingPattern or BoundRestPattern);
}
