using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Overloads;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Вывод аргументов generic-конструктора (spec §14): <c>unit Box&lt;T&gt;(T value)</c> и
/// <c>Box(5)</c> дают <c>Box&lt;i32&gt;</c>. Учитываются позиционные и именованные аргументы;
/// параметр, совпавший с <c>T</c> или содержащий его (например <c>list&lt;T&gt;</c>),
/// связывает <c>T</c>.
/// </summary>
public sealed partial class Binder
{
	private IReadOnlyList<KernType> InferConstructorTypeArguments(
		TypeSymbol type,
		IReadOnlyList<CallArgument> callArguments)
	{
		var count = type.GenericParameters.Count;
		var result = new KernType[count];
		Array.Fill(result, ErrorType.Instance);
		if (count == 0)
			return result;

		foreach (var constructor in type.Members.Constructors)
		{
			if (callArguments.Count > constructor.Parameters.Count)
				continue;

			var map = new Dictionary<string, KernType>(StringComparer.Ordinal);
			var ok = true;
			for (var index = 0; index < callArguments.Count && ok; index++)
			{
				var call = callArguments[index];
				ParameterSymbol? parameter = call.Name is null
					? index < constructor.Parameters.Count ? constructor.Parameters[index] : null
					: constructor.Parameters.FirstOrDefault(item => item.Name == call.Name);
				if (parameter is null)
				{
					ok = false;
					break;
				}

				ok = UnifyGeneric(parameter.Type, call.Type, map);
			}

			if (!ok || map.Count != count)
				continue;

			for (var index = 0; index < count; index++)
			{
				if (map.TryGetValue(type.GenericParameters[index].Name, out var inferred))
					result[index] = inferred;
			}

			return result;
		}

		return result;
	}

	/// <summary>Связывание параметра-типа с типом аргумента; конфликт — <c>false</c>.</summary>
	private static bool UnifyGeneric(KernType parameter, KernType argument, Dictionary<string, KernType> map)
	{
		switch (parameter)
		{
			case TypeParameterType typeParameter:
				if (map.TryGetValue(typeParameter.Name, out var existing))
					return existing.Equals(argument);
				map[typeParameter.Name] = argument;
				return true;

			case BuiltinGenericType builtin when argument is BuiltinGenericType other &&
				builtin.BuiltinKind == other.BuiltinKind &&
				builtin.Arguments.Count == other.Arguments.Count:
				for (var index = 0; index < builtin.Arguments.Count; index++)
				{
					if (!UnifyGeneric(builtin.Arguments[index], other.Arguments[index], map))
						return false;
				}

				return true;

			default:
				return true;
		}
	}

	/// <summary>Тип экземпляра с вычисленными аргументами (<c>unit</c> или <c>type</c>).</summary>
	private static KernType Instantiate(TypeSymbol type, IReadOnlyList<KernType> arguments) =>
		type.IsValueType
			? new UnitType(type, arguments, type.IsImmutable)
			: new ClassType(type, arguments);

	/// <summary>Кортежи совпадают по форме: имена полей не влияют на равенство типов.</summary>
	private static bool TupleShapesEqual(TupleType left, TupleType right)
	{
		if (left.Elements.Count != right.Elements.Count)
			return false;
		for (var index = 0; index < left.Elements.Count; index++)
		{
			if (!left.Elements[index].Type.Equals(right.Elements[index].Type))
				return false;
		}

		return true;
	}

	/// <summary>Карта аргументов generic-типа получателя: <c>T → i32</c> для <c>Box&lt;i32&gt;</c>.</summary>
	private static Dictionary<string, KernType> TypeArgumentsMap(KernType type)
	{
		var map = new Dictionary<string, KernType>(StringComparer.Ordinal);
		TypeSymbol? symbol = null;
		IReadOnlyList<KernType> arguments = [];
		switch (type)
		{
			case UnitType unit:
				symbol = unit.Symbol;
				arguments = unit.Arguments;
				break;
			case ClassType @class:
				symbol = @class.Symbol;
				arguments = @class.Arguments;
				break;
		}

		if (symbol is null)
			return map;

		for (var index = 0; index < symbol.GenericParameters.Count && index < arguments.Count; index++)
			map[symbol.GenericParameters[index].Name] = arguments[index];
		return map;
	}

	/// <summary>Подстановка выведенных аргументов в параметры конструктора для проверки вызова.</summary>
	private static FunctionSymbol SubstituteConstructor(
		FunctionSymbol constructor,
		IReadOnlyDictionary<string, KernType> map)
	{
		var parameters = new List<ParameterSymbol>(constructor.Parameters.Count);
		foreach (var parameter in constructor.Parameters)
			parameters.Add(parameter with { Type = Substitute(parameter.Type, map) });

		return new FunctionSymbol(constructor.Name, constructor.Kind)
		{
			Parameters = parameters,
			ReturnType = constructor.ReturnType is null ? null : Substitute(constructor.ReturnType, map),
			Owner = constructor.Owner,
			IsStatic = constructor.IsStatic,
			Accessibility = constructor.Accessibility,
			Declaration = constructor.Declaration,
		};
	}

	private static KernType Substitute(KernType type, IReadOnlyDictionary<string, KernType> map) => type switch
	{
		TypeParameterType parameter when map.TryGetValue(parameter.Name, out var concrete) => concrete,
		BuiltinGenericType builtin => new BuiltinGenericType(
			builtin.BuiltinKind,
			[.. builtin.Arguments.Select(argument => Substitute(argument, map))]),
		_ => type,
	};

	/// <summary>Прямое связывание позиционных аргументов с подставленными параметрами.</summary>
	private List<BoundExpression>? TryBindGenericConstructorArguments(
		FunctionSymbol constructor,
		IReadOnlyList<CallArgument> callArguments,
		Dictionary<CallArgument, BoundExpression> byCallArgument)
	{
		var ordered = new List<BoundExpression>(callArguments.Count);
		for (var index = 0; index < callArguments.Count; index++)
		{
			var call = callArguments[index];
			var parameter = constructor.Parameters[index];
			if (call.Name is not null)
				return null;
			if (!byCallArgument.TryGetValue(call, out var expression))
				return null;
			if (!Conversions.TryClassify(expression.Type, parameter.Type, out var conversion))
				return null;
			ordered.Add(ConvertWithConversion(expression, parameter.Type, conversion, expression.Span));
		}

		return ordered;
	}
}
