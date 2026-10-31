using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Overloads;

/// <summary>
/// Выбор перегрузки по spec §15: применимость → лучший кандидат → неоднозначность/неприменимость.
/// Возвращаемый тип в выборе не участвует; повторные сигнатуры отсеивает коллектор деклараций.
/// </summary>
public static class OverloadResolver
{
	public static OverloadResolution Resolve(IReadOnlyList<FunctionSymbol> candidates, CallArguments call)
	{
		ArgumentNullException.ThrowIfNull(candidates);
		ArgumentNullException.ThrowIfNull(call);

		if (candidates.Count == 0)
			return Failure("KRN1401", call, $"Нет подходящей перегрузки '{call.Name ?? "?"}': кандидатов нет");

		// Ошибка в аргументе не должна порождать каскад: выбираем первый кандидат без проверок.
		if (call.Arguments.Any(argument => argument.Type is ErrorType))
		{
			var function = candidates[0];
			return new OverloadResolution
			{
				Function = function,
				ReturnType = function.ReturnType
			};
		}

		var applicable = new List<Candidate>(candidates.Count);
		foreach (var function in candidates)
			if (TryBind(function, call, out var candidate))
				applicable.Add(candidate);

		if (applicable.Count == 0)
			return Failure("KRN1401", call, $"Нет подходящей перегрузки '{call.Name ?? "?"}': кандидаты — {Describe(candidates)}");

		var best = applicable[0];
		var tied = new List<Candidate> { best };
		foreach (var candidate in applicable.Skip(1))
		{
			var comparison = Compare(best, candidate, call.Arguments.Count);
			if (comparison < 0)
			{
				best = candidate;
				tied.Clear();
				tied.Add(candidate);
			}
			else if (comparison == 0)
			{
				tied.Add(candidate);
			}
		}

		if (tied.Count > 1)
			return Failure("KRN1402", call, $"Неоднозначный вызов '{call.Name ?? "?"}': подходят — {Describe(tied.Select(item => item.Function))}");

		return new OverloadResolution
		{
			Function = best.Function,
			Arguments = best.Arguments,
			TypeArguments = best.TypeArguments,
			ReceiverConversion = best.ReceiverConversion,
			ReturnType = best.ReturnType
		};
	}

	private static OverloadResolution Failure(string code, CallArguments call, string message) =>
		new()
		{
			Diagnostics = [new Diagnostic(code, message, call.Span)]
		};

	private static string Describe(IEnumerable<FunctionSymbol> candidates) =>
		string.Join(", ", candidates.Select(candidate => candidate.Signature));

	private static bool TryBind(FunctionSymbol function, CallArguments call, out Candidate candidate)
	{
		candidate = null!;
		var parameters = function.Parameters;

		var receiverTarget = ReceiverTarget(function);
		if (function.ReceiverType is not null && call.ReceiverType is null)
			return false;
		if (call.ReceiverType is not null && receiverTarget is null)
			return false;

		var varargParameter = -1;
		for (var index = 0; index < parameters.Count; index++)
			if (parameters[index].IsVararg)
			{
				varargParameter = index;
				break;
			}

		if (TryBindForm(function, call, expanded: false, varargParameter, out candidate))
			return true;

		return varargParameter >= 0 &&
			TryBindForm(function, call, expanded: true, varargParameter, out candidate);
	}

	private static bool TryBindForm(
		FunctionSymbol function,
		CallArguments call,
		bool expanded,
		int varargParameter,
		out Candidate candidate)
	{
		candidate = null!;
		var parameters = function.Parameters;

		int[] argumentToParameter;
		int defaultCount;
		var mapped = expanded
			? TryMapExpanded(call.Arguments, parameters, varargParameter, out argumentToParameter, out defaultCount)
			: TryMapNormal(call.Arguments, parameters, out argumentToParameter, out defaultCount);
		if (!mapped)
			return false;

		var receiverTarget = ReceiverTarget(function);
		var pairs = new List<(KernType Parameter, KernType Argument)>(call.Arguments.Count + 1);
		if (call.ReceiverType is not null && receiverTarget is not null)
			pairs.Add((receiverTarget, call.ReceiverType));

		for (var index = 0; index < call.Arguments.Count; index++)
		{
			var parameterIndex = argumentToParameter[index];
			if (parameterIndex < 0)
				return false;

			var declared = expanded && parameterIndex == varargParameter
				? ElementType(parameters[parameterIndex].Type)
				: parameters[parameterIndex].Type;
			pairs.Add((declared, call.Arguments[index].Type));
		}

		if (!GenericInference.TryInfer(
			function,
			pairs,
			call.ExplicitTypeArguments,
			ClassSubstitution(function, call.ReceiverType),
			out var substitution,
			out var typeArguments))
			return false;

		Conversion? receiverConversion = null;
		if (call.ReceiverType is not null)
		{
			var receiverType = TypeSubstitution.Apply(receiverTarget!, substitution);
			if (!Conversions.TryClassify(call.ReceiverType, receiverType, out var conversion))
				return false;
			receiverConversion = conversion;
		}

		var parameterTypes = new KernType[call.Arguments.Count];
		var conversions = new Conversion[call.Arguments.Count];
		for (var index = 0; index < call.Arguments.Count; index++)
		{
			var parameterIndex = argumentToParameter[index];
			var declared = expanded && parameterIndex == varargParameter
				? ElementType(parameters[parameterIndex].Type)
				: parameters[parameterIndex].Type;
			var target = TypeSubstitution.Apply(declared, substitution);
			parameterTypes[index] = target;

			if (!TryConvert(call.Arguments[index], target, out conversions[index]))
				return false;
		}

		var bound = new List<BoundArgument>(parameters.Count);
		for (var parameterIndex = 0; parameterIndex < parameters.Count; parameterIndex++)
		{
			if (expanded && parameterIndex == varargParameter)
			{
				for (var index = 0; index < call.Arguments.Count; index++)
					if (argumentToParameter[index] == parameterIndex)
						bound.Add(new BoundArgument(
							parameters[parameterIndex],
							call.Arguments[index],
							conversions[index],
							parameterIndex,
							true));
				continue;
			}

			var argumentIndex = Array.IndexOf(argumentToParameter, parameterIndex);
			bound.Add(argumentIndex >= 0
				? new BoundArgument(
					parameters[parameterIndex],
					call.Arguments[argumentIndex],
					conversions[argumentIndex],
					parameterIndex,
					false)
				: new BoundArgument(
					parameters[parameterIndex],
					null,
					Conversion.Implicit(ConversionKind.Identity),
					parameterIndex,
					false));
		}

		candidate = new Candidate
		{
			Function = function,
			Arguments = bound,
			ArgumentToParameter = argumentToParameter,
			ParameterTypes = parameterTypes,
			Conversions = conversions,
			TypeArguments = typeArguments,
			ReturnType = function.ReturnType is null ? null : TypeSubstitution.Apply(function.ReturnType, substitution),
			ReceiverConversion = receiverConversion,
			IsExpanded = expanded,
			DefaultCount = defaultCount
		};
		return true;
	}

	/// <summary>Тип приёмника: extension-получатель или владелец метода экземпляра.</summary>
	private static KernType? ReceiverTarget(FunctionSymbol function)
	{
		if (function.ReceiverType is not null)
			return function.ReceiverType;
		if (function.Owner is null || function.IsStatic)
			return null;
		// Аргументы владельца (не пустые!) нужны, чтобы после подстановки T = i32 приёмник
		// совпал с Stack<i32>; иначе конверсия получателя всегда падает.
		return function.Owner.IsValueType
			? new UnitType(function.Owner, [.. function.Owner.GenericParameters], function.Owner.IsImmutable)
			: new ClassType(function.Owner, [.. function.Owner.GenericParameters]);
	}

	/// <summary>
	/// Аргументы generic-параметров типа-владельца, выведенные из типа получателя:
	/// <c>Stack&lt;i32&gt;.push</c> видит <c>T = i32</c> (функция не объявляет T сама).
	/// </summary>
	private static IReadOnlyDictionary<string, KernType>? ClassSubstitution(
		FunctionSymbol function,
		KernType? receiverType)
	{
		if (function.Owner is not { } owner || receiverType is null)
			return null;

		IReadOnlyList<KernType>? arguments = receiverType switch
		{
			ClassType @class when ReferenceEquals(@class.Symbol, owner) => @class.Arguments,
			UnitType unit when ReferenceEquals(unit.Symbol, owner) => unit.Arguments,
			_ => null
		};
		if (arguments is null || arguments.Count == 0)
			return null;

		var map = new Dictionary<string, KernType>(StringComparer.Ordinal);
		for (var index = 0; index < owner.GenericParameterNames.Count && index < arguments.Count; index++)
			map[owner.GenericParameterNames[index]] = arguments[index];
		return map;
	}

	private static bool TryConvert(CallArgument argument, KernType target, out Conversion conversion)
	{
		if (argument.Constant is not null &&
			target is PrimitiveType primitive &&
			NumericInfo.TryGet(primitive.Primitive, out _))
			return Conversions.TryClassifyConstant(argument.Constant, primitive.Primitive, out conversion);

		return Conversions.TryClassify(argument.Type, target, out conversion);
	}

	private static bool TryMapNormal(
		IReadOnlyList<CallArgument> arguments,
		IReadOnlyList<ParameterSymbol> parameters,
		out int[] argumentToParameter,
		out int defaultCount)
	{
		argumentToParameter = CreateMap(arguments.Count);
		defaultCount = 0;
		var used = new bool[parameters.Count];
		var positional = 0;
		var sawNamed = false;

		for (var index = 0; index < arguments.Count; index++)
		{
			var argument = arguments[index];
			if (argument.IsPositional)
			{
				if (sawNamed)
					return false;
				if (positional >= parameters.Count)
					return false;

				var parameterIndex = positional++;
				if (used[parameterIndex])
					return false;

				argumentToParameter[index] = parameterIndex;
				used[parameterIndex] = true;
				continue;
			}

			sawNamed = true;
			var namedIndex = FindParameter(parameters, argument.Name!);
			if (namedIndex < 0 || used[namedIndex])
				return false;

			argumentToParameter[index] = namedIndex;
			used[namedIndex] = true;
		}

		defaultCount = 0;
		for (var index = 0; index < parameters.Count; index++)
		{
			if (used[index])
				continue;
			if (!parameters[index].HasDefault)
				return false;
			defaultCount++;
		}

		return true;
	}

	private static bool TryMapExpanded(
		IReadOnlyList<CallArgument> arguments,
		IReadOnlyList<ParameterSymbol> parameters,
		int varargParameter,
		out int[] argumentToParameter,
		out int defaultCount)
	{
		argumentToParameter = CreateMap(arguments.Count);
		defaultCount = 0;

		// Расширенная форма поддерживается только для последнего параметра: так проще сохранить
		// позиционный порядок и не изобретать правила для параметров после vararg.
		if (varargParameter != parameters.Count - 1)
			return false;

		var used = new bool[parameters.Count];
		var positional = 0;
		var sawNamed = false;

		for (var index = 0; index < arguments.Count; index++)
		{
			var argument = arguments[index];
			if (argument.IsPositional)
			{
				if (sawNamed)
					return false;

				if (positional < varargParameter)
				{
					if (used[positional])
						return false;
					argumentToParameter[index] = positional;
					used[positional] = true;
					positional++;
					continue;
				}

				argumentToParameter[index] = varargParameter;
				continue;
			}

			sawNamed = true;
			var namedIndex = FindParameter(parameters, argument.Name!);
			if (namedIndex < 0 || namedIndex >= varargParameter || used[namedIndex])
				return false;

			argumentToParameter[index] = namedIndex;
			used[namedIndex] = true;
		}

		for (var index = 0; index < varargParameter; index++)
		{
			if (used[index])
				continue;
			if (!parameters[index].HasDefault)
				return false;
			defaultCount++;
		}

		used[varargParameter] = true;
		return true;
	}

	private static int[] CreateMap(int count)
	{
		var map = new int[count];
		Array.Fill(map, -1);
		return map;
	}

	private static int FindParameter(IReadOnlyList<ParameterSymbol> parameters, string name)
	{
		for (var index = 0; index < parameters.Count; index++)
			if (parameters[index].Name == name)
				return index;
		return -1;
	}

	private static KernType ElementType(KernType type) =>
		type is BuiltinGenericType
		{
			BuiltinKind: BuiltinGenericKind.List or BuiltinGenericKind.Array or BuiltinGenericKind.Set
		} generic && generic.Arguments.Count == 1
			? generic.Arguments[0]
			: type;

	/// <summary>Сравнение кандидатов: 1 — левый лучше, -1 — правый лучше, 0 — равно.</summary>
	private static int Compare(Candidate left, Candidate right, int argumentCount)
	{
		var receiver = CompareConversion(left.ReceiverConversion, right.ReceiverConversion);
		if (receiver != 0)
			return receiver;

		var leftNoWorse = true;
		var rightNoWorse = true;
		var leftBetter = false;
		var rightBetter = false;

		for (var index = 0; index < argumentCount; index++)
		{
			var comparison = CompareConversion(
				left.Conversions[index],
				right.Conversions[index],
				left.ParameterTypes[index],
				right.ParameterTypes[index]);

			if (comparison > 0)
			{
				rightNoWorse = false;
				leftBetter = true;
			}
			else if (comparison < 0)
			{
				leftNoWorse = false;
				rightBetter = true;
			}
		}

		if (leftNoWorse && leftBetter)
			return 1;
		if (rightNoWorse && rightBetter)
			return -1;
		if (leftNoWorse != rightNoWorse)
			return leftNoWorse ? 1 : -1;

		if (left.Function.GenericArity == 0 && right.Function.GenericArity > 0)
			return 1;
		if (right.Function.GenericArity == 0 && left.Function.GenericArity > 0)
			return -1;

		if (left.IsExpanded != right.IsExpanded)
			return left.IsExpanded ? -1 : 1;

		if (left.DefaultCount != right.DefaultCount)
			return left.DefaultCount < right.DefaultCount ? 1 : -1;

		var specificity = CompareSpecificity(left, right, argumentCount);
		if (specificity != 0)
			return specificity;

		if (!left.Function.IsExtension && right.Function.IsExtension)
			return 1;
		if (left.Function.IsExtension && !right.Function.IsExtension)
			return -1;

		return CompareOwners(left.Function, right.Function);
	}

	private static int CompareConversion(Conversion? left, Conversion? right)
	{
		if (left is null || right is null)
			return 0;
		if (left.Value.Rank != right.Value.Rank)
			return left.Value.Rank < right.Value.Rank ? 1 : -1;
		return 0;
	}

	private static int CompareConversion(Conversion left, Conversion right, KernType? leftType, KernType? rightType)
	{
		if (left.Rank != right.Rank)
			return left.Rank < right.Rank ? 1 : -1;

		if (left.Kind != right.Kind || leftType is null || rightType is null)
			return 0;

		if (leftType.Equals(rightType))
			return 0;

		if (IsSignedInteger(leftType) && IsUnsignedInteger(rightType))
			return 1;
		if (IsUnsignedInteger(leftType) && IsSignedInteger(rightType))
			return -1;

		var leftToRight = Conversions.TryClassify(leftType, rightType, out _);
		var rightToLeft = Conversions.TryClassify(rightType, leftType, out _);
		if (leftToRight && !rightToLeft)
			return 1;
		if (rightToLeft && !leftToRight)
			return -1;

		return 0;
	}

	private static bool IsSignedInteger(KernType type) =>
		type is PrimitiveType primitive &&
		NumericInfo.TryGet(primitive.Primitive, out var info) &&
		info.IsInteger &&
		info.IsSigned;

	private static bool IsUnsignedInteger(KernType type) =>
		type is PrimitiveType primitive &&
		NumericInfo.TryGet(primitive.Primitive, out var info) &&
		info.IsInteger &&
		!info.IsSigned;

	private static int CompareSpecificity(Candidate left, Candidate right, int argumentCount)
	{
		var leftBetter = false;
		var rightBetter = false;

		for (var index = 0; index < argumentCount; index++)
		{
			var a = left.ParameterTypes[index];
			var b = right.ParameterTypes[index];
			if (a.Equals(b))
				continue;

			var aToB = Conversions.TryClassify(a, b, out _);
			var bToA = Conversions.TryClassify(b, a, out _);
			if (aToB && !bToA)
				leftBetter = true;
			else if (bToA && !aToB)
				rightBetter = true;
		}

		if (leftBetter && !rightBetter)
			return 1;
		if (rightBetter && !leftBetter)
			return -1;
		return 0;
	}

	private static int CompareOwners(FunctionSymbol left, FunctionSymbol right)
	{
		if (left.Owner is null || right.Owner is null || ReferenceEquals(left.Owner, right.Owner))
			return 0;

		if (DerivesFrom(left.Owner, right.Owner))
			return 1;
		if (DerivesFrom(right.Owner, left.Owner))
			return -1;
		return 0;
	}

	private static bool DerivesFrom(TypeSymbol symbol, TypeSymbol target)
	{
		var visited = new HashSet<TypeSymbol>();
		return DerivesFrom(symbol, target, visited);
	}

	private static bool DerivesFrom(TypeSymbol symbol, TypeSymbol target, HashSet<TypeSymbol> visited)
	{
		if (!visited.Add(symbol))
			return false;

		foreach (var baseType in symbol.Bases)
		{
			if (baseType is not ClassType classType)
				continue;
			if (classType.Symbol == target || DerivesFrom(classType.Symbol, target, visited))
				return true;
		}

		return false;
	}

	private sealed class Candidate
	{
		public required FunctionSymbol Function { get; init; }

		public required IReadOnlyList<BoundArgument> Arguments { get; init; }

		public required int[] ArgumentToParameter { get; init; }

		public required KernType[] ParameterTypes { get; init; }

		public required Conversion[] Conversions { get; init; }

		public required IReadOnlyList<KernType> TypeArguments { get; init; }

		public required KernType? ReturnType { get; init; }

		public required Conversion? ReceiverConversion { get; init; }

		public required bool IsExpanded { get; init; }

		public required int DefaultCount { get; init; }
	}
}
