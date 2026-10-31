namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Сопоставление типа-образца с фактическим типом (spec §10): приёмник расширения
/// <c>list&lt;T&gt;</c> связывает <c>T</c> с аргументом <c>list&lt;i32&gt;</c>. Свободные
/// параметры образца — <see cref="TypeParameterType"/>; остальные узлы сравниваются по форме.
/// </summary>
public static class TypePatternMatcher
{
	/// <summary>Возвращает связывание свободных параметров образца; пусто — образец без переменных.</summary>
	public static bool TryMatch(
		KernType pattern,
		KernType actual,
		out Dictionary<string, KernType> bindings)
	{
		ArgumentNullException.ThrowIfNull(pattern);
		ArgumentNullException.ThrowIfNull(actual);
		bindings = new Dictionary<string, KernType>(StringComparer.Ordinal);
		return TryMatchCore(pattern, actual, bindings);
	}

	private static bool TryMatchCore(KernType pattern, KernType actual, Dictionary<string, KernType> bindings)
	{
		if (pattern is ErrorType || actual is ErrorType)
			return true;

		switch (pattern)
		{
			case TypeParameterType parameter:
				if (bindings.TryGetValue(parameter.Name, out var bound))
					return bound.Equals(actual);
				bindings[parameter.Name] = actual;
				return true;

			case NullableType nullable when actual is NullableType actualNullable:
				return TryMatchCore(nullable.Underlying, actualNullable.Underlying, bindings);

			case BuiltinGenericType builtin when actual is BuiltinGenericType actualBuiltin &&
				builtin.BuiltinKind == actualBuiltin.BuiltinKind:
				return TryMatchArguments(builtin.Arguments, actualBuiltin.Arguments, bindings);

			case UnitType unit when actual is UnitType actualUnit && unit.Symbol == actualUnit.Symbol:
				return TryMatchArguments(unit.Arguments, actualUnit.Arguments, bindings);

			case ClassType @class when actual is ClassType actualClass && @class.Symbol == actualClass.Symbol:
				return TryMatchArguments(@class.Arguments, actualClass.Arguments, bindings);

			case TupleType tuple when actual is TupleType actualTuple &&
				tuple.Elements.Count == actualTuple.Elements.Count:
				for (var index = 0; index < tuple.Elements.Count; index++)
					if (!TryMatchCore(tuple.Elements[index].Type, actualTuple.Elements[index].Type, bindings))
						return false;
				return true;

			case FunctionType function when actual is FunctionType actualFunction &&
				function.Parameters.Count == actualFunction.Parameters.Count:
				for (var index = 0; index < function.Parameters.Count; index++)
					if (!TryMatchCore(function.Parameters[index], actualFunction.Parameters[index], bindings))
						return false;
				return TryMatchCore(function.ReturnType, actualFunction.ReturnType, bindings);

			default:
				return pattern.Equals(actual);
		}
	}

	private static bool TryMatchArguments(
		IReadOnlyList<KernType> pattern,
		IReadOnlyList<KernType> actual,
		Dictionary<string, KernType> bindings)
	{
		if (pattern.Count != actual.Count)
			return false;

		for (var index = 0; index < pattern.Count; index++)
			if (!TryMatchCore(pattern[index], actual[index], bindings))
				return false;

		return true;
	}
}
