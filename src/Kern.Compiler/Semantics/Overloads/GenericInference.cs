using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;

namespace Kern.Compiler.Semantics.Overloads;

/// <summary>
/// Вывод generic-аргументов из типов фактических аргументов (spec §15): нижние границы
/// объединяются в общий тип, затем проверяются ограничения. Ошибка вывода делает кандидата
/// неприменимым, а не диагностикой сама по себе.
/// </summary>
internal static class GenericInference
{
	public static bool TryInfer(
		FunctionSymbol function,
		IReadOnlyList<(KernType Parameter, KernType Argument)> pairs,
		IReadOnlyList<KernType>? explicitArguments,
		IReadOnlyDictionary<string, KernType>? classSubstitution,
		out Dictionary<string, KernType> substitution,
		out IReadOnlyList<KernType> typeArguments)
	{
		substitution = classSubstitution is { Count: > 0 }
			? new Dictionary<string, KernType>(classSubstitution, StringComparer.Ordinal)
			: new Dictionary<string, KernType>(StringComparer.Ordinal);
		typeArguments = [];
		var parameters = function.GenericParameters;

		if (parameters.Count == 0)
			return explicitArguments is not { Count: > 0 };

		if (explicitArguments is not null)
		{
			if (explicitArguments.Count != parameters.Count)
				return false;

			for (var index = 0; index < parameters.Count; index++)
				substitution[parameters[index].Name] = explicitArguments[index];
		}
		else
		{
			var bounds = new Dictionary<string, List<KernType>>(StringComparer.Ordinal);
			foreach (var (parameter, argument) in pairs)
				Collect(parameter, argument, parameters, bounds);

			var inferred = new List<KernType>(parameters.Count);
			foreach (var parameter in parameters)
			{
				if (!bounds.TryGetValue(parameter.Name, out var candidates) || candidates.Count == 0)
					return false;

				var merged = MergeBounds(candidates);
				if (merged is null)
					return false;

				inferred.Add(merged);
				substitution[parameter.Name] = merged;
			}
			typeArguments = inferred;
		}

		foreach (var parameter in parameters)
		{
			if (!substitution.TryGetValue(parameter.Name, out var actual))
				return false;

			if (parameter.RequiresConstructor &&
				!HasUsableConstructor(actual, parameter.ConstructorParameters))
				return false;

			foreach (var constraint in parameter.Constraints)
			{
				var substituted = TypeSubstitution.Apply(constraint, substitution);
				if (!Satisfies(actual, substituted))
					return false;
			}
		}

		if (typeArguments.Count == 0)
		{
			var inferred = new List<KernType>(parameters.Count);
			foreach (var parameter in parameters)
				inferred.Add(substitution[parameter.Name]);
			typeArguments = inferred;
		}

		return true;
	}

	private static void Collect(
		KernType parameter,
		KernType argument,
		IReadOnlyList<TypeParameterType> declared,
		Dictionary<string, List<KernType>> bounds)
	{
		if (argument is ErrorType)
			return;

		if (parameter is TypeParameterType typeParameter)
		{
			if (!declared.Any(item => item.Name == typeParameter.Name))
				return;

			if (!bounds.TryGetValue(typeParameter.Name, out var list))
				bounds.Add(typeParameter.Name, list = []);
			list.Add(argument);
			return;
		}

		switch (parameter)
		{
			case NullableType nullable:
				Collect(nullable.Underlying, argument is NullableType actualNullable ? actualNullable.Underlying : argument, declared, bounds);
				break;

			case ClassType parameterClass when argument is ClassType argumentClass &&
				parameterClass.Symbol == argumentClass.Symbol &&
				parameterClass.Arguments.Count == argumentClass.Arguments.Count:
				for (var index = 0; index < parameterClass.Arguments.Count; index++)
					Collect(parameterClass.Arguments[index], argumentClass.Arguments[index], declared, bounds);
				break;

			case UnitType parameterUnit when argument is UnitType argumentUnit &&
				parameterUnit.Symbol == argumentUnit.Symbol &&
				parameterUnit.Arguments.Count == argumentUnit.Arguments.Count:
				for (var index = 0; index < parameterUnit.Arguments.Count; index++)
					Collect(parameterUnit.Arguments[index], argumentUnit.Arguments[index], declared, bounds);
				break;

			case BuiltinGenericType parameterBuiltin when argument is BuiltinGenericType argumentBuiltin &&
				parameterBuiltin.BuiltinKind == argumentBuiltin.BuiltinKind &&
				parameterBuiltin.Arguments.Count == argumentBuiltin.Arguments.Count:
				for (var index = 0; index < parameterBuiltin.Arguments.Count; index++)
					Collect(parameterBuiltin.Arguments[index], argumentBuiltin.Arguments[index], declared, bounds);
				break;

			case TupleType parameterTuple when argument is TupleType argumentTuple &&
				parameterTuple.Elements.Count == argumentTuple.Elements.Count:
				for (var index = 0; index < parameterTuple.Elements.Count; index++)
					Collect(parameterTuple.Elements[index].Type, argumentTuple.Elements[index].Type, declared, bounds);
				break;

			case FunctionType parameterFunction when argument is FunctionType argumentFunction &&
				parameterFunction.Parameters.Count == argumentFunction.Parameters.Count:
				for (var index = 0; index < parameterFunction.Parameters.Count; index++)
					Collect(parameterFunction.Parameters[index], argumentFunction.Parameters[index], declared, bounds);
				Collect(parameterFunction.ReturnType, argumentFunction.ReturnType, declared, bounds);
				break;
		}
	}

	private static KernType? MergeBounds(IReadOnlyList<KernType> bounds)
	{
		var distinct = bounds.Where(bound => bound is not ErrorType).ToList();
		if (distinct.Count == 0)
			return ErrorType.Instance;

		var result = distinct[0];
		for (var index = 1; index < distinct.Count; index++)
		{
			var next = distinct[index];
			if (result.Equals(next))
				continue;

			if (result is PrimitiveType left && next is PrimitiveType right)
			{
				var common = CommonNumeric(left.Primitive, right.Primitive);
				if (common is null)
					return null;
				result = PrimitiveType.For(common.Value);
				continue;
			}

			if (Conversions.TryClassify(next, result, out _))
				continue;

			if (Conversions.TryClassify(result, next, out _))
			{
				result = next;
				continue;
			}

			return null;
		}

		return result;
	}

	private static PrimitiveKind? CommonNumeric(PrimitiveKind left, PrimitiveKind right)
	{
		if (left == right)
			return left;

		if (!NumericInfo.TryGet(left, out var first) || !NumericInfo.TryGet(right, out var second))
			return null;

		if (first.IsUniversal || second.IsUniversal)
		{
			if (second.IsUniversal)
				return SatisfiesUniversal(PrimitiveType.For(left), second.Kind) ? left : null;
			return SatisfiesUniversal(PrimitiveType.For(right), first.Kind) ? right : null;
		}

		if (first.IsFloat || second.IsFloat)
		{
			if (first.IsFloat && second.IsFloat)
				return first.Bits >= second.Bits ? left : right;
			return PrimitiveKind.F64;
		}

		if (first.IsInteger && second.IsInteger)
		{
			if (first.IsSigned == second.IsSigned)
				return first.Bits >= second.Bits ? left : right;

			var signedBits = first.IsSigned ? first.Bits : second.Bits;
			var unsignedBits = first.IsSigned ? second.Bits : first.Bits;
			var bits = System.Math.Max(signedBits, unsignedBits + 1);
			return bits switch
			{
				<= 8 => PrimitiveKind.I8,
				<= 16 => PrimitiveKind.I16,
				<= 32 => PrimitiveKind.I32,
				<= 64 => PrimitiveKind.I64,
				_ => null
			};
		}

		return null;
	}

	private static bool Satisfies(KernType actual, KernType constraint)
	{
		if (actual is ErrorType || constraint is ErrorType)
			return true;

		if (constraint is PrimitiveType primitive &&
			primitive.Primitive is PrimitiveKind.Inum or PrimitiveKind.Fnum or PrimitiveKind.Num)
			return SatisfiesUniversal(actual, primitive.Primitive);

		return Conversions.TryClassify(actual, constraint, out _);
	}

	/// <summary>
	/// Ограничение <c>ctor()</c> (spec §14): нужен нестатический конструктор; класс не должен
	/// быть абстрактным. Встроенные (<c>list&lt;T&gt;</c>) и статические типы не подходят.
	/// </summary>
	private static bool HasUsableConstructor(KernType actual, IReadOnlyList<KernType> required)
	{
		if (actual is ErrorType)
			return true;

		TypeSymbol? symbol = actual switch
		{
			ClassType @class => @class.Symbol,
			UnitType unit => unit.Symbol,
			_ => null
		};
		if (symbol is null || symbol.IsAbstract || symbol.IsStatic)
			return false;

		return symbol.Members.Constructors.Any(constructor =>
			!constructor.IsStatic &&
			(required.Count == 0 || MatchesConstructor(constructor, required)));
	}

	/// <summary>
	/// <c>ctor(i32, str)</c>: арность и совместимость типов параметров. Каждый
	/// объявленный аргумент должен приводиться к типу параметра конструктора.
	/// </summary>
	private static bool MatchesConstructor(FunctionSymbol constructor, IReadOnlyList<KernType> required)
	{
		if (constructor.Parameters.Count != required.Count)
			return false;
		for (var index = 0; index < required.Count; index++)
		{
			if (!Conversions.TryClassify(required[index], constructor.Parameters[index].Type, out _))
				return false;
		}

		return true;
	}

	private static bool SatisfiesUniversal(KernType actual, PrimitiveKind universal)
	{
		if (actual is not PrimitiveType { } primitive || !NumericInfo.TryGet(primitive.Primitive, out var info))
			return false;

		return universal switch
		{
			PrimitiveKind.Inum => info.IsInteger,
			PrimitiveKind.Fnum => info.IsFloat,
			PrimitiveKind.Num => info.IsInteger || info.IsFloat,
			_ => false
		};
	}
}
