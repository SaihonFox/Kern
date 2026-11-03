using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>
	/// Q12: статический анализ match после связывания ветвей. Предупреждения KRN2110/KRN2111
	/// не меняют bound-дерево: непокрытое значение по-прежнему паникует MatchFail в рантайме.
	/// </summary>
	private void AnalyzeMatch(BoundExpression? target, IReadOnlyList<BoundMatchArm> arms, TextSpan span)
	{
		AnalyzeUnreachableArms(arms);
		if (target is null || arms.Count == 0)
			return;

		var subject = target.Type;
		if (IsError(subject) || subject is PrimitiveType { Primitive: PrimitiveKind.Never })
			return;
		if (HasCatchAllArm(arms))
			return;

		AnalyzeExhaustiveness(subject, arms, span);
	}

	/// <summary>KRN2111: ветвь после безусловной `_` или повтор уже покрытого литерала/типа.</summary>
	private void AnalyzeUnreachableArms(IReadOnlyList<BoundMatchArm> arms)
	{
		var literals = new List<BoundLiteralPattern>();
		var types = new List<KernType>();
		var hasNil = false;
		var hasCatchAll = false;

		foreach (var arm in arms)
		{
			if (hasCatchAll)
			{
				Warn("KRN2111", "Ветвь недостижима: более ранняя ветвь покрывает все значения", arm.Span);
				continue;
			}

			// Ветвь с guard никого не покрывает и сама может быть недостижимой только после `_`.
			if (arm.Guard is not null)
				continue;

			// `_`, связывание, or-паттерн с такой альтернативой или кортеж из них покрывают всё.
			if (IsCatchAllPattern(arm.Pattern))
			{
				hasCatchAll = true;
				continue;
			}

			switch (arm.Pattern)
			{
				case BoundOrPattern or:
					// B4a2: альтернативы учитываются по отдельности; wildcard внутри or-паттерна
					// делает его catch-all, поэтому следующие ветви недостижимы.
					foreach (var alternative in or.Alternatives)
					{
						switch (alternative)
						{
							case BoundLiteralPattern literal:
								literals.Add(literal);
								break;
							case BoundTypePattern type:
								types.Add(type.Type);
								break;
							case BoundNilPattern:
								hasNil = true;
								break;
						}
					}
					break;
				case BoundLiteralPattern literal:
					if (literals.Any(previous => SameLiteral(previous, literal)))
						Warn("KRN2111", "Ветвь недостижима: это значение уже покрыто более ранней ветвью", arm.Span);
					else
						literals.Add(literal);
					break;
				case BoundTypePattern type:
					if (types.Any(previous => previous.Equals(type.Type)))
						Warn("KRN2111", $"Ветвь недостижима: тип '{type.Type}' уже покрыт более ранней ветвью", arm.Span);
					else
						types.Add(type.Type);
					break;
				case BoundNilPattern:
					if (hasNil)
						Warn("KRN2111", "Ветвь недостижима: nil уже покрыт более ранней ветвью", arm.Span);
					else
						hasNil = true;
					break;
			}
		}
	}

	/// <summary>KRN2110: по типу субъекта считаем, каких значений не хватает.</summary>
	private void AnalyzeExhaustiveness(KernType subject, IReadOnlyList<BoundMatchArm> arms, TextSpan span)
	{
		switch (subject)
		{
			case NullableType nullable:
				WarnMissingCoverage(span, MissingForNullable(nullable, arms));
				break;
			case UnionType union:
				WarnMissingCoverage(span, MissingForUnion(union, arms));
				break;
			case PrimitiveType { Primitive: PrimitiveKind.Bool }:
				WarnMissingCoverage(span, MissingForBool(arms));
				break;
			default:
				Warn("KRN2110", "Match не покрывает все случаи: добавьте ветвь '_'", span);
				break;
		}
	}

	private void WarnMissingCoverage(TextSpan span, List<string> missing)
	{
		if (missing.Count == 0)
			return;

		var names = string.Join(", ", missing.Select(name => $"'{name}'"));
		Warn("KRN2110", $"Match не покрывает все случаи: не хватает ветвей для {names}", span);
	}

	private static List<string> MissingForBool(IReadOnlyList<BoundMatchArm> arms)
	{
		var hasTrue = false;
		var hasFalse = false;
		foreach (var arm in arms)
		{
			if (arm.Guard is not null)
				continue;
			foreach (var pattern in PatternAlternatives(arm.Pattern))
			{
				if (!TryGetLiteral(pattern, out var value, out _))
					continue;

				if (value is true)
					hasTrue = true;
				else if (value is false)
					hasFalse = true;
			}
		}

		var missing = new List<string>(2);
		if (!hasTrue)
			missing.Add("true");
		if (!hasFalse)
			missing.Add("false");
		return missing;
	}

	private static List<string> MissingForUnion(UnionType union, IReadOnlyList<BoundMatchArm> arms)
	{
		var missing = new List<string>();
		foreach (var variant in union.Alternatives)
		{
			if (VariantCovered(variant, arms))
				continue;

			var name = variant.ToString();
			if (!missing.Contains(name))
				missing.Add(name);
		}

		return missing;
	}

	private static List<string> MissingForNullable(NullableType nullable, IReadOnlyList<BoundMatchArm> arms)
	{
		var hasNil = false;
		var hasValue = false;
		foreach (var arm in arms)
		{
			if (arm.Guard is not null)
				continue;

			foreach (var pattern in PatternAlternatives(arm.Pattern))
			{
				switch (pattern)
				{
					case BoundNilPattern:
						hasNil = true;
						break;
					case BoundTypePattern type when CoversType(type.Type, nullable.Underlying):
					case BoundConstructorPattern constructor when CoversType(constructor.Type, nullable.Underlying):
					case BoundStructuralPattern structural when CoversType(structural.Type, nullable.Underlying):
						hasValue = true;
						break;
				}
			}
		}

		var missing = new List<string>(2);
		if (!hasNil)
			missing.Add("nil");
		if (!hasValue)
			missing.Add(nullable.Underlying.ToString());
		return missing;
	}

	/// <summary>
	/// Покрыт ли вариант union хотя бы одной безусловной ветвью: тип/конструктор/структурный
	/// паттерн этого варианта, `nil` для nullable-варианта или оба литерала bool.
	/// </summary>
	private static bool VariantCovered(KernType variant, IReadOnlyList<BoundMatchArm> arms)
	{
		var hasTrue = false;
		var hasFalse = false;
		foreach (var arm in arms)
		{
			if (arm.Guard is not null)
				continue;

			foreach (var pattern in PatternAlternatives(arm.Pattern))
			{
				switch (pattern)
				{
					case BoundTypePattern type when CoversType(type.Type, variant):
					case BoundConstructorPattern constructor when CoversType(constructor.Type, variant):
					case BoundStructuralPattern structural when CoversType(structural.Type, variant):
						return true;
					case BoundNilPattern when variant is NullableType:
						return true;
					case BoundLiteralPattern when variant is PrimitiveType { Primitive: PrimitiveKind.Bool }:
						if (TryGetLiteral(pattern, out var value, out _))
						{
							if (value is true)
								hasTrue = true;
							else if (value is false)
								hasFalse = true;
						}
						break;
				}
			}
		}

		return variant is PrimitiveType { Primitive: PrimitiveKind.Bool } && hasTrue && hasFalse;
	}

	/// <summary>
	/// Может ли паттерн типа <paramref name="patternType"/> выделить все значения варианта:
	/// тот же тип, тот же generic-символ или базовый тип варианта.
	/// </summary>
	private static bool CoversType(KernType patternType, KernType variant)
	{
		if (variant is NullableType nullableVariant)
			return CoversType(patternType, nullableVariant.Underlying);

		if (patternType.Equals(variant))
			return true;
		if (TypeSymbolOf(patternType) is { } patternSymbol && TypeSymbolOf(variant) is { } variantSymbol)
			return patternSymbol.Equals(variantSymbol);

		return patternType is (ClassType or UnitType) && variant is (ClassType or UnitType) &&
			Conversions.TryClassify(variant, patternType, out _);
	}

	private static TypeSymbol? TypeSymbolOf(KernType type) => type switch
	{
		UnitType unit => unit.Symbol,
		ClassType klass => klass.Symbol,
		_ => null
	};

	private static bool HasCatchAllArm(IReadOnlyList<BoundMatchArm> arms) =>
		arms.Any(arm => arm.Guard is null && IsCatchAllPattern(arm.Pattern));

	/// <summary>B4a2: паттерн и все альтернативы or-паттерна (рекурсивно) для анализа покрытия.</summary>
	private static IEnumerable<BoundPattern> PatternAlternatives(BoundPattern pattern) =>
		pattern is BoundOrPattern or
			? or.Alternatives.SelectMany(PatternAlternatives)
			: [pattern];

	/// <summary>
	/// Ловит все значения: <c>_</c>, связывание, кортеж из таких паттернов или or-паттерн
	/// с такой альтернативой.
	/// </summary>
	private static bool IsCatchAllPattern(BoundPattern pattern) =>
		PatternAlternatives(pattern).Any(IsCatchAllAlternative);

	private static bool IsCatchAllAlternative(BoundPattern pattern) => pattern switch
	{
		BoundWildcardPattern or BoundBindingPattern => true,
		BoundTuplePattern tuple => tuple.Items.Count > 0 && tuple.Items.All(IsCatchAllPattern),
		_ => false
	};

	private static bool SameLiteral(BoundLiteralPattern left, BoundLiteralPattern right) =>
		TryGetLiteral(left, out var leftValue, out var leftType) &&
		TryGetLiteral(right, out var rightValue, out var rightType) &&
		leftType.Equals(rightType) &&
		Equals(leftValue, rightValue);

	/// <summary>Значение и итоговый тип литерального паттерна (неявные преобразования разворачиваются).</summary>
	private static bool TryGetLiteral(BoundPattern pattern, out object? value, out KernType type)
	{
		value = null;
		type = PrimitiveType.Void;
		if (pattern is not BoundLiteralPattern literal)
			return false;

		var expression = literal.Value;
		while (expression is BoundConversionExpression conversion)
			expression = conversion.Operand;

		if (expression is not BoundLiteralExpression bound)
			return false;

		value = bound.Value;
		type = bound.Type;
		return true;
	}
}
