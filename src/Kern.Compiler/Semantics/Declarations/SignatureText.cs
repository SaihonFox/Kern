using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.Semantics.Declarations;

/// <summary>
/// Канонический текст сигнатуры для идентичности перегрузок (spec §15). Имена generic-параметров
/// метода заменяются позициями <c>!0</c>, <c>!1</c>: <c>fun f&lt;T&gt;(T)</c> и
/// <c>fun f&lt;U&gt;(U)</c> — одна и та же сигнатура.
/// </summary>
internal static class SignatureText
{
	public static string For(FunctionSymbol function)
	{
		var builder = new System.Text.StringBuilder(function.Name);
		if (function.GenericArity > 0)
			builder.Append('<').Append(function.GenericArity).Append('>');
		builder.Append('(');
		builder.AppendJoin(",", function.Parameters.Select(parameter => Type(parameter.Type, function.GenericParameters)));
		builder.Append(')');
		return builder.ToString();
	}

	public static string Type(KernType type, IReadOnlyList<TypeParameterType> methodParameters) => type switch
	{
		TypeParameterType parameter => Parameter(parameter, methodParameters),
		NullableType nullable => $"{Type(nullable.Underlying, methodParameters)}?",
		ClassType classType => Named(classType.Symbol.Name, classType.Arguments, methodParameters),
		UnitType unitType => Named(unitType.Symbol.Name, unitType.Arguments, methodParameters),
		BuiltinGenericType builtin => Named(
			BuiltinGenericType.GetCanonicalName(builtin.BuiltinKind),
			builtin.Arguments,
			methodParameters),
		UnionType union => $"[{string.Join(",", union.Alternatives.Select(item => Type(item, methodParameters)))}]",
		TupleType tuple => $"({string.Join(",", tuple.Elements.Select(element => TupleElement(element, methodParameters)))})",
		FunctionType function => $"fun({string.Join(",", function.Parameters.Select(item => Type(item, methodParameters)))})->{Type(function.ReturnType, methodParameters)}",
		_ => type.ToString()
	};

	private static string Parameter(TypeParameterType parameter, IReadOnlyList<TypeParameterType> methodParameters)
	{
		for (var index = 0; index < methodParameters.Count; index++)
			if (methodParameters[index].Name == parameter.Name)
				return $"!{index}";
		return parameter.Name;
	}

	private static string TupleElement(TupleElement element, IReadOnlyList<TypeParameterType> methodParameters) =>
		element.Name is null
			? Type(element.Type, methodParameters)
			: $"{Type(element.Type, methodParameters)} {element.Name}";

	private static string Named(string name, IReadOnlyList<KernType> arguments, IReadOnlyList<TypeParameterType> methodParameters) =>
		arguments.Count == 0
			? name
			: $"{name}<{string.Join(",", arguments.Select(item => Type(item, methodParameters)))}>";
}
