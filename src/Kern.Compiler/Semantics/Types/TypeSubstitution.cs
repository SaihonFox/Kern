namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Подстановка аргументов вместо generic-параметров в тип (spec §14). Используется выводом
/// перегрузок и проверкой наследования; рекурсивно обходит все составные типы.
/// </summary>
internal static class TypeSubstitution
{
	public static KernType Apply(KernType type, IReadOnlyDictionary<string, KernType> substitution)
	{
		if (substitution.Count == 0)
			return type;

		return type switch
		{
			TypeParameterType parameter when substitution.TryGetValue(parameter.Name, out var replacement) => replacement,
			ClassType classType => new ClassType(
				classType.Symbol,
				[.. classType.Arguments.Select(argument => Apply(argument, substitution))]),
			UnitType unitType => new UnitType(
				unitType.Symbol,
				[.. unitType.Arguments.Select(argument => Apply(argument, substitution))],
				unitType.IsImmutable),
			BuiltinGenericType builtin => new BuiltinGenericType(
				builtin.BuiltinKind,
				[.. builtin.Arguments.Select(argument => Apply(argument, substitution))]),
			NullableType nullable => NullableType.Create(Apply(nullable.Underlying, substitution)),
			UnionType union => UnionType.Create([.. union.Alternatives.Select(alternative => Apply(alternative, substitution))]),
			TupleType tuple => new TupleType(
				[.. tuple.Elements.Select(element => new TupleElement(Apply(element.Type, substitution), element.Name))]),
			FunctionType function => new FunctionType(
				[.. function.Parameters.Select(parameter => Apply(parameter, substitution))],
				Apply(function.ReturnType, substitution)),
			_ => type
		};
	}
}
