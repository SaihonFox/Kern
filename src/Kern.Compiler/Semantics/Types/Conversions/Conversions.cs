namespace Kern.Compiler.Semantics.Types.Conversions;

/// <summary>
/// Классификация неявных преобразований между разрешёнными типами (spec §3, §15;
/// docs/design/M2-types.md §4). Порядок проверок фиксирован: первое совпадение выигрывает.
/// </summary>
public static class Conversions
{
	/// <summary>Неявное преобразование в контексте присваивания/аргумента.</summary>
	public static bool TryClassify(KernType source, KernType target, out Conversion conversion) =>
		Classify(source, target, allowContextual: false, out conversion);

	/// <summary>
	/// То же плюс контекстные <c>str</c> ↔ <c>list&lt;char&gt;</c> (spec §3, §15): они выбираются
	/// только ожидаемым типом и никогда не выводятся сами.
	/// </summary>
	public static bool TryClassifyContextual(KernType source, KernType target, out Conversion conversion) =>
		Classify(source, target, allowContextual: true, out conversion);

	/// <summary>
	/// Константное преобразование литерала без суффикса (правило C#, spec §15, DECISIONS §C):
	/// значение должно помещаться в целевой тип. Литерал с суффиксом имеет точный тип и может
	/// только расширяться как обычное числовое значение.
	/// </summary>
	public static bool TryClassifyConstant(NumericLiteralValue literal, PrimitiveKind target, out Conversion conversion)
	{
		conversion = default;
		ArgumentNullException.ThrowIfNull(literal);
		if (!NumericInfo.TryGet(target, out _))
			return false;

		if (literal.ExactType is { } exact)
		{
			if (exact == target)
			{
				conversion = Conversion.Implicit(ConversionKind.Identity);
				return true;
			}

			if (NumericRules.IsWidening(exact, target))
			{
				conversion = Conversion.Implicit(ConversionKind.NumericWidening);
				return true;
			}

			return false;
		}

		if (literal.IsFloat)
		{
			var fits = target == PrimitiveKind.Fnum ||
				target == PrimitiveKind.Num ||
				(NumericRules.IsFloat(target) && NumericRules.FitsFloat(literal.FloatValue, target));
			if (!fits)
				return false;

			conversion = Conversion.Implicit(ConversionKind.NumericConstant);
			return true;
		}

		// Целый литерал обязан помещаться хотя бы в i64/u64 (как в C#); иначе он не является
		// допустимым целым литералом и не может стать f64 по «диапазону».
		if (literal.IntegerValue < long.MinValue || literal.IntegerValue > ulong.MaxValue)
			return false;

		if (target is PrimitiveKind.Inum or PrimitiveKind.Num)
		{
			conversion = Conversion.Implicit(ConversionKind.NumericConstant);
			return true;
		}

		var integerFits = NumericRules.IsIntegral(target)
			? NumericRules.FitsInteger(literal.IntegerValue, target)
			: target is PrimitiveKind.F16 or PrimitiveKind.F32 or PrimitiveKind.F64 &&
				NumericRules.FitsIntegerAsFloat(literal.IntegerValue, target);
		if (!integerFits)
			return false;

		conversion = Conversion.Implicit(ConversionKind.NumericConstant);
		return true;
	}

	/// <summary>
	/// <c>nil</c> допустим только для nullable-типа (spec §3, §4; Q9, docs/design/M2-types.md):
	/// union, даже содержащий nullable-альтернативу, сам nullable-типом не является.
	/// </summary>
	public static bool IsNilAssignable(KernType target) => target is NullableType;

	private static bool Classify(KernType source, KernType target, bool allowContextual, out Conversion conversion)
	{
		conversion = default;
		if (source is null || target is null)
			return false;

		// Ошибка не порождает каскада: совместима с чем угодно.
		if (source is ErrorType || target is ErrorType)
			return Succeed(ConversionKind.ErrorSource, out conversion);

		if (source is PrimitiveType { Primitive: PrimitiveKind.Never })
			return Succeed(ConversionKind.NeverSource, out conversion);

		if (source.Equals(target))
			return Succeed(ConversionKind.Identity, out conversion);

		// `none` — внутреннее состояние, а не записываемое значение (spec §4):
		// ни в какой другой тип, включая object, оно не преобразуется.
		if (source is PrimitiveType { Primitive: PrimitiveKind.None })
			return false;

		// nil -> T? (и union, где есть nullable-альтернатива); nil -> object запрещён (Q9).
		if (source is NilType)
			return IsNilAssignable(target)
				? Succeed(ConversionKind.NilToNullable, out conversion)
				: false;

		// T -> T?; T? -> T? уже покрыто identity, T? -> T не неявное.
		if (target is NullableType nullable)
		{
			if (source is NullableType)
				return false;

			// Сначала прямая обёртка (identity), затем подъём S -> T? при S -> T (Q8).
			if (source.Equals(nullable.Underlying))
				return Succeed(ConversionKind.NullableWrap, out conversion);

			return Classify(source, nullable.Underlying, allowContextual: false, out _)
				? Succeed(ConversionKind.Lifted, out conversion)
				: false;
		}

		if (source is PrimitiveType sourcePrimitive &&
			target is PrimitiveType targetPrimitive &&
			NumericRules.IsWidening(sourcePrimitive.Primitive, targetPrimitive.Primitive))
			return Succeed(ConversionKind.NumericWidening, out conversion);

		// Упаковка: любое значение и T? -> object.
		if (target is PrimitiveType { Primitive: PrimitiveKind.Object })
			return Succeed(ConversionKind.Boxing, out conversion);

		if (source is ClassType sourceClass &&
			target is ClassType targetClass &&
			IsClassUpcast(sourceClass, targetClass))
			return Succeed(ConversionKind.BaseUpcast, out conversion);

		if (source is UnitType sourceUnit &&
			target is UnitType targetUnit &&
			sourceUnit.IsImmutable &&
			!targetUnit.IsImmutable &&
			sourceUnit.Symbol == targetUnit.Symbol &&
			sourceUnit.Arguments.SequenceEqual(targetUnit.Arguments))
			return Succeed(ConversionKind.UnitImmutableErase, out conversion);

		if (TryCollectionUpcast(source, target, out var collectionKind))
			return Succeed(collectionKind, out conversion);

		if (allowContextual)
		{
			if (source is PrimitiveType { Primitive: PrimitiveKind.Str } && IsListOfChar(target))
				return Succeed(ConversionKind.StrToListChar, out conversion);

			if (target is PrimitiveType { Primitive: PrimitiveKind.Str } && IsListOfChar(source))
				return Succeed(ConversionKind.ListCharToStr, out conversion);
		}

		if (source is UnionType sourceUnion)
		{
			if (target is UnionType targetUnion)
			{
				foreach (var alternative in sourceUnion.Alternatives)
					if (!targetUnion.Alternatives.Any(candidate => TryClassify(alternative, candidate, out _)))
						return false;

				return Succeed(ConversionKind.UnionUpcast, out conversion);
			}

			foreach (var alternative in sourceUnion.Alternatives)
				if (!TryClassify(alternative, target, out _))
					return false;

			return Succeed(ConversionKind.UnionUpcast, out conversion);
		}

		if (target is UnionType unionTarget)
		{
			return unionTarget.Alternatives.Any(alternative => TryClassify(source, alternative, out _))
				? Succeed(ConversionKind.UnionTarget, out conversion)
				: false;
		}

		if (source is TupleType sourceTuple && target is TupleType targetTuple)
		{
			if (sourceTuple.Elements.Count != targetTuple.Elements.Count)
				return false;

			for (var index = 0; index < sourceTuple.Elements.Count; index++)
				if (!TryClassify(sourceTuple.Elements[index].Type, targetTuple.Elements[index].Type, out _))
					return false;

			return Succeed(ConversionKind.TupleUpcast, out conversion);
		}

		if (source is FunctionType sourceFunction && target is FunctionType targetFunction)
			return IsSameSignature(sourceFunction, targetFunction)
				? Succeed(ConversionKind.FunctionUpcast, out conversion)
				: false;

		return false;
	}

	/// <summary>
	/// Тождество подписи функции: Q3 по умолчанию — без вариантности (ни параметры, ни результат
	/// не преобразуются). На практике срабатывает identity выше, ветка оставлена явной.
	/// </summary>
	private static bool IsSameSignature(FunctionType source, FunctionType target)
	{
		if (source.Parameters.Count != target.Parameters.Count)
			return false;

		for (var index = 0; index < source.Parameters.Count; index++)
			if (!source.Parameters[index].Equals(target.Parameters[index]))
				return false;

		return source.ReturnType.Equals(target.ReturnType);
	}

	private static bool IsClassUpcast(ClassType source, ClassType target)
	{
		if (source.Symbol == target.Symbol)
			return SameArguments(source.Arguments, target.Arguments);

		return ReachesBase(
			source.Symbol,
			source.Arguments,
			target.Symbol,
			target.Arguments,
			[]);
	}

	/// <summary>
	/// Посещённые узлы ключуются парой (символ, аргументы): при ромбе
	/// <c>Two[Mid&lt;str&gt;, Mid&lt;i32&gt;]</c> это разные узлы, и <c>Base&lt;i32&gt;</c> находится.
	/// </summary>
	private static bool ReachesBase(
		TypeSymbol symbol,
		IReadOnlyList<KernType> arguments,
		TypeSymbol targetSymbol,
		IReadOnlyList<KernType> targetArguments,
		HashSet<(TypeSymbol Symbol, string Arguments)> visited)
	{
		if (!visited.Add(VisitKey(symbol, arguments)))
			return false;

		var substitution = BuildSubstitution(symbol.GenericParameterNames, arguments);
		foreach (var baseType in symbol.Bases)
		{
			if (Substitute(baseType, substitution) is not ClassType baseClass)
				continue;

			if (baseClass.Symbol == targetSymbol && SameArguments(baseClass.Arguments, targetArguments))
				return true;

			if (ReachesBase(baseClass.Symbol, baseClass.Arguments, targetSymbol, targetArguments, visited))
				return true;
		}

		return false;
	}

	private static (TypeSymbol Symbol, string Arguments) VisitKey(
		TypeSymbol symbol,
		IReadOnlyList<KernType> arguments) =>
		(symbol, string.Join(",", arguments.Select(argument => argument.ToString())));

	private static Dictionary<string, KernType> BuildSubstitution(
		IReadOnlyList<string> names,
		IReadOnlyList<KernType> arguments)
	{
		var count = System.Math.Min(names.Count, arguments.Count);
		var substitution = new Dictionary<string, KernType>(count, StringComparer.Ordinal);
		for (var index = 0; index < count; index++)
			substitution[names[index]] = arguments[index];

		return substitution;
	}

	/// <summary>Подставляет аргументы объявления вместо его generic-параметров в тип-родитель.</summary>
	private static KernType Substitute(KernType type, Dictionary<string, KernType> substitution) =>
		TypeSubstitution.Apply(type, substitution);

	private static bool TryCollectionUpcast(KernType source, KernType target, out ConversionKind kind)
	{
		kind = ConversionKind.CollectionUpcast;
		if (target is not BuiltinGenericType { BuiltinKind: BuiltinGenericKind.Collection } collection ||
			collection.Arguments.Count != 1)
			return false;

		var element = collection.Arguments[0];
		if (source is PrimitiveType { Primitive: PrimitiveKind.Str })
			return element.Equals(PrimitiveType.Char);

		if (source is not BuiltinGenericType builtin ||
			builtin.BuiltinKind is not (BuiltinGenericKind.List or BuiltinGenericKind.Array or BuiltinGenericKind.Set) ||
			builtin.Arguments.Count != 1)
			return false;

		return TryClassify(builtin.Arguments[0], element, out _);
	}

	private static bool IsListOfChar(KernType type) =>
		type is BuiltinGenericType { BuiltinKind: BuiltinGenericKind.List } list &&
		list.Arguments.Count == 1 &&
		list.Arguments[0].Equals(PrimitiveType.Char);

	private static bool SameArguments(IReadOnlyList<KernType> left, IReadOnlyList<KernType> right)
	{
		if (left.Count != right.Count)
			return false;

		for (var index = 0; index < left.Count; index++)
			if (!left[index].Equals(right[index]))
				return false;

		return true;
	}

	private static bool Succeed(ConversionKind kind, out Conversion conversion)
	{
		conversion = Conversion.Implicit(kind);
		return true;
	}
}
