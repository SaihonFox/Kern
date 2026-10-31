using Kern.Compiler.Lexing;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>
	/// Связывает синтаксический паттерн с типом субъекта. <paramref name="subject"/> = null
	/// только в targetless-match, где допустимы wildcard и чистое условие.
	/// </summary>
	private BoundPattern BindPattern(PatternNode pattern, KernType? subject, List<BoundPatternBinding> bindings) => pattern switch
	{
		WildcardPattern wildcard => new BoundWildcardPattern(wildcard.Span),
		IdentifierPattern identifier => BindIdentifierPattern(identifier, subject, bindings),
		LiteralPattern literal => BindLiteralPattern(literal, subject),
		TypePattern type => BindTypePattern(type, subject, bindings),
		ConstructorPattern constructor => BindConstructorPattern(constructor, subject, bindings),
		StructuralPattern structural => BindStructuralPattern(structural, subject, bindings),
		BindingPattern binding => BindBindingPattern(binding, subject, bindings),
		ExpressionPattern expression => BindExpressionPattern(expression),
		RangePattern range => BindRangePattern(range, subject),
		OrPattern or => BindOrPattern(or, subject, bindings),
		ListPattern list => BindListPattern(list, subject, bindings),
		RestPattern rest => UnsupportedPattern(rest, "rest-паттерн"),
		_ => UnsupportedPattern(pattern, pattern.GetType().Name)
	};

	/// <summary>
	/// B4a2: or-паттерн <c>p1 | p2</c>. Альтернативы связываются по отдельности; связывание
	/// имён внутри or-паттерна в этом срезе не поддержано (KRN2114), чтобы не изобретать
	/// перенос слотов между альтернативами.
	/// </summary>
	private BoundPattern BindOrPattern(OrPattern pattern, KernType? subject, List<BoundPatternBinding> bindings)
	{
		var alternatives = new List<BoundPattern>(pattern.Alternatives.Count);
		foreach (var alternative in pattern.Alternatives)
		{
			if (OrAlternativeBinds(alternative))
			{
				Report("KRN2114", "Связывание имён в or-паттернах пока не поддержано", alternative.Span);
				continue;
			}

			if (!IsSupportedOrAlternative(alternative))
			{
				Report("KRN2114", "В or-паттерне пока поддержаны только литералы, диапазоны, типы, nil и '_'", alternative.Span);
				continue;
			}

			alternatives.Add(BindPattern(alternative, subject, bindings));
		}

		return alternatives.Count == 0
			? new BoundUnsupportedPattern(pattern.Span)
			: new BoundOrPattern(alternatives, pattern.Span);
	}

	/// <summary>
	/// B4b1/B4b2: паттерн списка <c>[p1, p2]</c> / <c>[a, ..rest, z]</c>. Субъект обязан быть
	/// <c>list&lt;T&gt;</c>/<c>array&lt;T&gt;</c>; элементы связываются с типом <c>T</c>.
	/// Rest <c>..</c> без имени игнорирует остаток, <c>..name</c> связывает его как <c>list&lt;T&gt;</c>
	/// (копия) и для list-, и для array-субъекта. Допускается не более одного rest'а (KRN2113).
	/// </summary>
	private BoundPattern BindListPattern(
		ListPattern pattern,
		KernType? subject,
		List<BoundPatternBinding> bindings)
	{
		KernType elementType = ErrorType.Instance;
		if (subject is not null && !IsError(subject))
		{
			if (CollectionElementType(subject) is { } collectionElement)
				elementType = collectionElement;
			else
				Report("KRN2112", $"Паттерн списка требует list/array, получено '{subject}'", pattern.Span);
		}

		RestPattern? rest = null;
		foreach (var item in pattern.Items)
		{
			if (item is not RestPattern candidate)
				continue;

			if (rest is not null)
			{
				Report("KRN2113", "В паттерне списка допускается только один rest-паттерн '..'", candidate.Span);
				continue;
			}

			rest = candidate;
		}

		var itemsBeforeRest = -1;
		var items = new List<BoundPattern>(pattern.Items.Count);
		foreach (var item in pattern.Items)
		{
			if (item is RestPattern)
			{
				if (itemsBeforeRest < 0)
					itemsBeforeRest = items.Count;
				continue;
			}

			items.Add(BindPattern(item, elementType, bindings));
		}

		BoundPatternBinding? restBinding = null;
		if (rest?.Name is { } restName)
		{
			var restType = new BuiltinGenericType(BuiltinGenericKind.List, [elementType]);
			var variable = DeclarePatternBinding(restName, restType, rest.Span, bindings);
			restBinding = new BoundPatternBinding(restName, variable.Slot, restType, rest.Span);
		}

		return new BoundListPattern(items, itemsBeforeRest, elementType, restBinding, pattern.Span);
	}

	/// <summary>Связывает ли альтернатива имя (в or-паттерне это пока запрещено).</summary>
	private bool OrAlternativeBinds(PatternNode pattern) => pattern switch
	{
		BindingPattern => true,
		IdentifierPattern identifier => identifier.Name is not ("_" or "true" or "false" or "nil") &&
			!IsTypeName(identifier.Name),
		TypePattern type => type.Name.Length > 0 && type.Name != "_",
		ConstructorPattern constructor => constructor.Arguments.Any(OrAlternativeBinds),
		StructuralPattern structural => structural.RestName is not null ||
			structural.Fields.Any(field => field.Pattern is not null && OrAlternativeBinds(field.Pattern)),
		_ => false
	};

	/// <summary>Виды альтернатив, которые срез B4a2 принимает в or-паттерне.</summary>
	private bool IsSupportedOrAlternative(PatternNode pattern) => pattern switch
	{
		LiteralPattern or RangePattern or TypePattern or WildcardPattern => true,
		IdentifierPattern identifier => !OrAlternativeBinds(identifier),
		_ => false
	};

	private BoundPattern BindIdentifierPattern(
		IdentifierPattern pattern,
		KernType? subject,
		List<BoundPatternBinding> bindings)
	{
		if (pattern.Name is "true" or "false")
		{
			var value = pattern.Name == "true";
			return BindLiteralValuePattern(
				new BoundLiteralExpression(value, null, PrimitiveType.Bool, pattern.Span),
				subject,
				pattern.Span);
		}

		if (pattern.Name == "nil")
			return BindNilPattern(pattern, subject);

		// Голое имя типа (`str`, `Circle`) — паттерн проверки типа без связывания.
		if (IsTypeName(pattern.Name))
		{
			var type = _resolver.Resolve(NamedTypeSyntax.Plain(pattern.Name, pattern.Span), _typeScope);
			return BindTypeOnlyPattern(type, subject, pattern.Span);
		}

		var bindingType = subject ?? ErrorType.Instance;
		var variable = DeclarePatternBinding(pattern.Name, bindingType, pattern.Span, bindings);
		return new BoundBindingPattern(pattern.Name, variable.Slot, bindingType, pattern.Span);
	}

	private BoundPattern BindLiteralPattern(LiteralPattern pattern, KernType? subject)
	{
		if (pattern.Text is "true" or "false")
		{
			var value = pattern.Text == "true";
			return BindLiteralValuePattern(
				new BoundLiteralExpression(value, null, PrimitiveType.Bool, pattern.Span),
				subject,
				pattern.Span);
		}

		if (pattern.Text == "nil")
			return BindNilPattern(pattern, subject);

		var literal = pattern.Kind switch
		{
			TokenKind.IntegerLiteral or TokenKind.NumberLiteral =>
				BindNumericLiteral(pattern.Text, pattern.Span),
			TokenKind.StringLiteral => BindStringLiteral(pattern.Text, pattern.Span),
			TokenKind.CharLiteral => BindCharLiteral(pattern.Text, pattern.Span),
			_ => new BoundErrorExpression(pattern.Span)
		};

		return BindLiteralValuePattern(literal, subject, pattern.Span);
	}

	private BoundPattern BindLiteralValuePattern(BoundExpression literal, KernType? subject, TextSpan span)
	{
		if (!IsValuePatternCompatible(subject, literal.Type))
		{
			Report("KRN2112", $"Литеральный паттерн '{literal.Type}' несовместим с типом субъекта '{subject}'", span);
			return new BoundLiteralPattern(literal, span);
		}

		// Литерал сравнивается со значением субъекта, поэтому приводим его к типу субъекта
		// (например, `match i64 { 5 => … }`): codegen иначе сравнил бы i32 с i64.
		if (subject is not null && !IsError(subject) && !subject.Equals(literal.Type) &&
			Conversions.TryClassify(literal.Type, subject, out var conversion))
		{
			literal = new BoundConversionExpression(literal, conversion, subject, span);
		}

		return new BoundLiteralPattern(literal, span);
	}

	/// <summary>
	/// B4a: диапазон значений <c>1..=5</c> / <c>1..5</c> / <c>'a'..='z'</c>. Обе границы —
	/// литералы, представимые типом субъекта; плавающие и строковые диапазоны не поддержаны.
	/// </summary>
	private BoundPattern BindRangePattern(RangePattern pattern, KernType? subject)
	{
		if (pattern.Start is not LiteralPattern startLiteral || pattern.End is not LiteralPattern endLiteral)
		{
			Report("KRN2114", "Границы диапазона в паттерне должны быть литералами", pattern.Span);
			return new BoundUnsupportedPattern(pattern.Span);
		}

		var start = BindRangeBoundLiteral(startLiteral);
		var end = BindRangeBoundLiteral(endLiteral);
		var startValid = CheckRangeBound(start, startLiteral, subject);
		var endValid = CheckRangeBound(end, endLiteral, subject);
		if (!startValid || !endValid)
			return new BoundUnsupportedPattern(pattern.Span);

		var type = subject is null || IsError(subject) ? start.Type : subject;
		if (type is PrimitiveType
			{
				Primitive: PrimitiveKind.F16 or PrimitiveKind.F32 or PrimitiveKind.F64 or PrimitiveKind.Fnum or PrimitiveKind.Str
			})
		{
			Report("KRN2114", $"Диапазон значений типа '{type}' пока не поддержан (B4a)", pattern.Span);
			return new BoundUnsupportedPattern(pattern.Span);
		}

		start = CoerceRangeBound(start, subject, pattern.Span);
		end = CoerceRangeBound(end, subject, pattern.Span);

		// Пустой (для `..`) или перевёрнутый диапазон проверяем по значениям литералов.
		if (TryCompareRangeBounds(start, end, out var comparison) &&
			(pattern.IsInclusive ? comparison > 0 : comparison >= 0))
		{
			Report("KRN2113", "Некорректный диапазон в паттерне: пустой или перевёрнутый диапазон", pattern.Span);
			return new BoundUnsupportedPattern(pattern.Span);
		}

		return new BoundRangePattern(start, end, pattern.IsInclusive, type, pattern.Span);
	}

	/// <summary>Связывает литерал границы без проверки совместимости с субъектом.</summary>
	private BoundExpression BindRangeBoundLiteral(LiteralPattern literal) => literal.Kind switch
	{
		TokenKind.IntegerLiteral or TokenKind.NumberLiteral => BindNumericLiteral(literal.Text, literal.Span),
		TokenKind.StringLiteral => BindStringLiteral(literal.Text, literal.Span),
		TokenKind.CharLiteral => BindCharLiteral(literal.Text, literal.Span),
		_ => new BoundErrorExpression(literal.Span)
	};

	/// <summary>Граница диапазона должна быть представима типом субъекта (KRN2112).</summary>
	private bool CheckRangeBound(BoundExpression bound, LiteralPattern literal, KernType? subject)
	{
		if (IsValuePatternCompatible(subject, bound.Type))
			return true;

		Report(
			"KRN2112",
			$"Граница диапазона '{literal.Text}' типа '{bound.Type}' несовместима с типом субъекта '{subject}'",
			literal.Span);
		return false;
	}

	/// <summary>Приводит границу к типу субъекта так же, как литеральный паттерн.</summary>
	private static BoundExpression CoerceRangeBound(BoundExpression bound, KernType? subject, TextSpan span)
	{
		if (subject is null || IsError(subject) || subject.Equals(bound.Type))
			return bound;

		return Conversions.TryClassify(bound.Type, subject, out var conversion)
			? new BoundConversionExpression(bound, conversion, subject, span)
			: bound;
	}

	/// <summary>Сравнивает значения границ для проверки пустого/перевёрнутого диапазона.</summary>
	private static bool TryCompareRangeBounds(BoundExpression start, BoundExpression end, out int comparison)
	{
		comparison = 0;
		if (UnwrapRangeBound(start) is not BoundLiteralExpression { Value: { } left } ||
			UnwrapRangeBound(end) is not BoundLiteralExpression { Value: { } right })
			return false;

		switch (left)
		{
			case System.Numerics.BigInteger leftInteger when right is System.Numerics.BigInteger rightInteger:
				comparison = leftInteger.CompareTo(rightInteger);
				return true;
			case char leftChar when right is char rightChar:
				comparison = leftChar.CompareTo(rightChar);
				return true;
			case string leftString when right is string rightString:
				comparison = string.CompareOrdinal(leftString, rightString);
				return true;
			default:
				return false;
		}
	}

	private static BoundExpression UnwrapRangeBound(BoundExpression expression) =>
		expression is BoundConversionExpression conversion ? UnwrapRangeBound(conversion.Operand) : expression;

	private BoundPattern BindNilPattern(PatternNode pattern, KernType? subject)
	{
		if (subject is null || IsError(subject) || subject is NullableType)
			return new BoundNilPattern(subject ?? ErrorType.Instance, pattern.Span);

		if (subject is UnionType union && union.Alternatives.FirstOrDefault(alternative => alternative is NullableType) is { } nullable)
			return new BoundNilPattern(nullable, pattern.Span);

		Report("KRN2112", $"Паттерн nil требует nullable-тип, получено '{subject}'", pattern.Span);
		return new BoundNilPattern(subject, pattern.Span);
	}

	private BoundPattern BindTypePattern(
		TypePattern pattern,
		KernType? subject,
		List<BoundPatternBinding> bindings)
	{
		var type = _resolver.Resolve(pattern.Type, _typeScope);
		if (IsError(type))
			return new BoundTypePattern(type, null, pattern.Span);

		CheckPatternCompatible(subject, type, pattern.Span, "типа");
		if (pattern.Name.Length == 0 || pattern.Name == "_")
			return new BoundTypePattern(type, null, pattern.Span);

		var variable = DeclarePatternBinding(pattern.Name, type, pattern.Span, bindings);
		var binding = new BoundBindingPattern(pattern.Name, variable.Slot, type, pattern.Span);
		return new BoundTypePattern(type, binding, pattern.Span);
	}

	private BoundPattern BindTypeOnlyPattern(
		KernType type,
		KernType? subject,
		TextSpan span)
	{
		if (IsError(type))
			return new BoundTypePattern(type, null, span);

		CheckPatternCompatible(subject, type, span, "типа");
		return new BoundTypePattern(type, null, span);
	}

	/// <summary>
	/// Позиционный паттерн unit-типа <c>Point(x, y)</c>. B4b2: последним аргументом допустим
	/// безымянный rest <c>..</c> — он принимает и игнорирует все оставшиеся поля, поэтому
	/// арность проверяется как «объявлено не меньше, чем перечислено».
	/// </summary>
	private BoundPattern BindConstructorPattern(
		ConstructorPattern pattern,
		KernType? subject,
		List<BoundPatternBinding> bindings)
	{
		var type = _resolver.Resolve(NamedTypeSyntax.Plain(pattern.Name, pattern.Span), _typeScope);
		if (IsError(type))
			return new BoundUnsupportedPattern(pattern.Span);
		if (type is not UnitType unit)
		{
			Report("KRN2113", $"Конструкторский паттерн '{pattern.Name}' требует unit-тип, получено '{type}'", pattern.Span);
			return new BoundUnsupportedPattern(pattern.Span);
		}

		CheckPatternCompatible(subject, type, pattern.Span, "типа");
		var declared = unit.Symbol.Members.Fields
			.Where(field => !field.IsStatic && !field.IsBackingField)
			.ToList();

		var restCount = pattern.Arguments.Count(argument => argument is RestPattern);
		if (restCount > 1)
			Report("KRN2113", "В паттерне допускается только один rest-паттерн '..'", pattern.Span);
		var hasRest = restCount > 0;
		if (hasRest && pattern.Arguments[^1] is not RestPattern)
			Report("KRN2114", "Rest-паттерн '..' в позиционном паттерне допустим только последним", pattern.Span);

		var positional = pattern.Arguments.Where(argument => argument is not RestPattern).ToList();
		foreach (var argument in pattern.Arguments)
		{
			if (argument is RestPattern { Name: { } restName })
				Report(
					"KRN2114",
					$"Связывание rest-паттерна '..{restName}' в паттерне '{unit}' пока не поддержано",
					argument.Span);
		}

		if (hasRest ? positional.Count > declared.Count : positional.Count != declared.Count)
			Report(
				"KRN2113",
				$"Паттерн '{unit}' ожидает {declared.Count} позиционных аргументов, получено {positional.Count}",
				pattern.Span);

		var fields = new List<BoundFieldPattern>(positional.Count);
		for (var index = 0; index < positional.Count; index++)
		{
			var field = index < declared.Count ? declared[index] : null;
			var name = field?.Name ?? $"_{index}";
			var fieldType = field?.Type ?? ErrorType.Instance;
			var nested = BindPattern(positional[index], fieldType, bindings);
			fields.Add(new BoundFieldPattern(name, fieldType, nested, positional[index].Span));
		}

		return new BoundConstructorPattern(type, fields, pattern.Span);
	}

	private BoundPattern BindStructuralPattern(
		StructuralPattern pattern,
		KernType? subject,
		List<BoundPatternBinding> bindings)
	{
		var type = _resolver.Resolve(pattern.Type, _typeScope);
		if (IsError(type))
			return new BoundUnsupportedPattern(pattern.Span);
		if (type is not UnitType unit)
		{
			Report("KRN2113", $"Структурный паттерн '{type}' требует unit-тип", pattern.Span);
			return new BoundUnsupportedPattern(pattern.Span);
		}

		CheckPatternCompatible(subject, type, pattern.Span, "типа");
		if (pattern.HasRest)
		{
			if (pattern.RestCount > 1)
				Report("KRN2113", "В паттерне допускается только один rest-паттерн '..'", pattern.Span);
			if (pattern.RestName is { } restName)
				Report(
					"KRN2114",
					$"Связывание rest-паттерна '..{restName}' в структурном паттерне пока не поддержано",
					pattern.Span);
		}

		var names = new HashSet<string>(StringComparer.Ordinal);
		var fields = new List<BoundFieldPattern>(pattern.Fields.Count);
		foreach (var fieldPattern in pattern.Fields)
		{
			var field = unit.Symbol.Members.FindField(fieldPattern.Name);
			var fieldType = field?.Type ?? ErrorType.Instance;
			if (!names.Add(fieldPattern.Name))
				Report("KRN2113", $"Поле '{fieldPattern.Name}' указано в паттерне '{unit}' несколько раз", fieldPattern.Span);
			if (field is null)
				Report("KRN2113", $"У типа '{unit}' нет поля '{fieldPattern.Name}'", fieldPattern.Span);

			var nested = fieldPattern.Pattern is null
				? new BoundWildcardPattern(fieldPattern.Span)
				: BindPattern(fieldPattern.Pattern, fieldType, bindings);
			fields.Add(new BoundFieldPattern(fieldPattern.Name, fieldType, nested, fieldPattern.Span));
		}

		return new BoundStructuralPattern(type, fields, pattern.HasRest, pattern.Span);
	}

	private BoundPattern BindBindingPattern(
		BindingPattern pattern,
		KernType? subject,
		List<BoundPatternBinding> bindings)
	{
		var type = subject ?? ErrorType.Instance;
		var variable = DeclarePatternBinding(pattern.Name, type, pattern.Span, bindings);
		return new BoundBindingPattern(pattern.Name, variable.Slot, type, pattern.Span);
	}

	private BoundPattern BindExpressionPattern(ExpressionPattern pattern)
	{
		// Парсер targetless-match отдаёт `_` как выражение, если за ним сразу идёт `=>`.
		if (pattern.Expression is IdentifierExpression { Name: "_" })
			return new BoundWildcardPattern(pattern.Span);

		var condition = BindExpression(pattern.Expression);
		if (!IsBool(condition.Type) && !IsError(condition.Type))
			Report("KRN2102", $"Условие в match должно быть bool, получено '{condition.Type}'", pattern.Expression.Span);
		return new BoundGuardPattern(condition, pattern.Span);
	}

	private BoundVariable DeclarePatternBinding(
		string name,
		KernType type,
		TextSpan span,
		List<BoundPatternBinding> bindings)
	{
		var variable = new BoundVariable(name, type, _nextSlot++, isSingleAssignment: true) { Frame = _frame };
		if (!_scope.TryDeclare(variable))
			Report("KRN2106", $"Переменная '{name}' уже объявлена в этом паттерне", span);

		_flow.MarkAssigned(variable.Slot);
		bindings.Add(new BoundPatternBinding(name, variable.Slot, type, span));
		return variable;
	}

	private void CheckPatternCompatible(KernType? subject, KernType pattern, TextSpan span, string kind)
	{
		if (subject is null || IsPatternCompatible(subject, pattern))
			return;

		Report("KRN2112", $"Паттерн {kind} '{pattern}' несовместим с типом субъекта '{subject}'", span);
	}

	/// <summary>Может ли значение субъекта оказаться значением типа паттерна.</summary>
	private static bool IsPatternCompatible(KernType subject, KernType pattern)
	{
		if (IsError(subject) || IsError(pattern) || subject.Equals(pattern))
			return true;

		if (subject is UnionType union)
			return union.Alternatives.Any(alternative => IsPatternCompatible(alternative, pattern));

		if (subject is NullableType nullable && pattern is not NullableType)
			return IsPatternCompatible(nullable.Underlying, pattern);

		if (pattern is UnionType patternUnion)
			return patternUnion.Alternatives.Any(alternative => IsPatternCompatible(subject, alternative));

		return Conversions.TryClassify(pattern, subject, out _) ||
			Conversions.TryClassify(subject, pattern, out _);
	}

	/// <summary>Литерал должен быть представим типом субъекта (без обратного направления).</summary>
	private static bool IsValuePatternCompatible(KernType? subject, KernType value)
	{
		if (subject is null || IsError(subject) || IsError(value))
			return true;

		return Conversions.TryClassify(value, subject, out _);
	}

	private bool IsTypeName(string name) =>
		TypeCatalog.TryGetPrimitive(name, out _) ||
		TypeCatalog.TryGetBuiltin(name, out _) ||
		_module.FindType(name) is not null;

	private BoundPattern UnsupportedPattern(PatternNode pattern, string description)
	{
		Report("KRN2114", $"Паттерн '{description}' пока не поддержан (M3-E)", pattern.Span);
		return new BoundUnsupportedPattern(pattern.Span);
	}
}
