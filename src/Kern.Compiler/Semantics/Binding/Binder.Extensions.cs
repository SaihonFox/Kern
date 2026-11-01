using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Расширения типов (spec §10): <c>fun str.shout()</c>, <c>fun i32.double</c>,
/// <c>op i32.*(Point)</c>. Группы собирает <see cref="DeclarationCollector"/>, а байндер
/// подключает их к вызовам/доступу/операторам после обычных и нативных членов.
/// </summary>
public sealed partial class Binder
{
	/// <summary>Привязка тел расширений (без неё у функций нет байткода).</summary>
	private void BindExtensions(List<BoundFunction> functions)
	{
		foreach (var group in _module.Extensions.Values)
		{
			foreach (var function in group.Functions)
				if (function.Body is not null)
					functions.Add(BindFunction(function));

			foreach (var property in group.Properties)
			{
				if (property.Getter is { Body: not null } getter)
					functions.Add(BindFunction(getter));
				if (property.Setter is { Body: not null } setter)
					functions.Add(BindFunction(setter));
				if (property.Init is { Body: not null } init)
					functions.Add(BindFunction(init));
			}
		}
	}

	/// <summary>
	/// Пользовательский или расширяющий оператор для пары операндов (spec §12). Ищется
	/// сначала среди расширений левого типа (включая <c>op i32.*(Point)</c>), затем среди
	/// объявленных операторов типа. <c>null</c> — оператор не найден, решает встроенный путь.
	/// </summary>
	private BoundExpression? TryBindUserOperator(
		string symbol,
		BoundExpression left,
		BoundExpression right,
		TextSpan span)
	{
		foreach (var group in _moduleScope.FindExtensionsFor(left.Type))
		{
			if (!TypePatternMatcher.TryMatch(group.ReceiverType, left.Type, out var bindings))
				continue;

			foreach (var function in group.Functions)
			{
				if (!IsUnaryOrBinaryOperator(function, symbol, arity: 1))
					continue;
				if (TryBindOperatorCall(function, left, right, span, bindings) is { } call)
					return call;
			}
		}

		var owner = OwnerOf(left.Type);
		if (owner is not null)
		{
			foreach (var function in owner.Members.Operators)
			{
				if (!IsUnaryOrBinaryOperator(function, symbol, arity: 1))
					continue;
				if (TryBindOperatorCall(function, left, right, span) is { } call)
					return call;
			}
		}

		return TryBindNativeOperator(symbol, left, right, span);
	}

	/// <summary>
	/// Оператор нативного типа (<c>[KernMember(Operator = "+")]</c> в <c>Kern.Runtime</c>):
	/// <c>duration + duration</c>, <c>duration * i64</c>, <c>datetime - datetime</c>. Приёмник
	/// может неявно приводиться к типу self (например, <c>i32 * duration</c> → self <c>i64</c>).
	/// </summary>
	private BoundExpression? TryBindNativeOperator(
		string symbol,
		BoundExpression left,
		BoundExpression right,
		TextSpan span)
	{
		if (_module.NativeFunctions.Count == 0)
			return null;

		foreach (FunctionSymbol function in _module.NativeFunctions)
		{
			if (function.Kind != FunctionKind.Operator ||
				function.OpSymbol != symbol ||
				function.NativeSelfType is null ||
				function.Parameters.Count != 1)
				continue;

			var receiver = left;
			IReadOnlyDictionary<string, KernType>? bindings = NativeReceiverBindings(function.NativeSelfType, left.Type);
			if (!NativeReceiverMatches(function.NativeSelfType, left.Type))
			{
				if (!Conversions.TryClassify(left.Type, function.NativeSelfType, out var receiverConversion))
					continue;
				receiver = ConvertWithConversion(left, function.NativeSelfType, receiverConversion, left.Span);
			}

			if (TryBindOperatorCall(function, receiver, right, span, bindings) is { } call)
				return call;
		}

		return null;
	}

	/// <summary>
	/// Аргументы generic-параметров нативного получателя: <c>event&lt;T&gt;</c> + <c>event&lt;str&gt;</c>
	/// дают <c>T → str</c>. Нужно операторам нативных generic-типов (<c>ev + handler</c>).
	/// </summary>
	private static IReadOnlyDictionary<string, KernType>? NativeReceiverBindings(KernType expected, KernType actual)
	{
		if (expected is BuiltinGenericType expectedBuiltin &&
			actual is BuiltinGenericType actualBuiltin &&
			expectedBuiltin.BuiltinKind == actualBuiltin.BuiltinKind &&
			expectedBuiltin.Arguments.Count == actualBuiltin.Arguments.Count)
		{
			var builtinMap = new Dictionary<string, KernType>(StringComparer.Ordinal);
			for (var index = 0; index < expectedBuiltin.Arguments.Count; index++)
				if (expectedBuiltin.Arguments[index] is TypeParameterType parameter)
					builtinMap[parameter.Name] = actualBuiltin.Arguments[index];
			return builtinMap.Count == 0 ? null : builtinMap;
		}

		if (expected is not ClassType { Symbol.Kind: Semantics.Types.SymbolKind.Native } expectedClass ||
			actual is not ClassType actualClass ||
			!ReferenceEquals(expectedClass.Symbol, actualClass.Symbol) ||
			expectedClass.Arguments.Count != actualClass.Arguments.Count)
			return null;

		var map = new Dictionary<string, KernType>(StringComparer.Ordinal);
		for (var index = 0; index < expectedClass.Arguments.Count; index++)
		{
			if (expectedClass.Arguments[index] is TypeParameterType parameter)
				map[parameter.Name] = actualClass.Arguments[index];
		}

		return map.Count == 0 ? null : map;
	}

	private static bool IsUnaryOrBinaryOperator(FunctionSymbol function, string symbol, int arity) =>
		function.Kind == FunctionKind.Operator &&
		function.OpSymbol == symbol &&
		function.Parameters.Count == arity;

	private BoundExpression? TryBindOperatorCall(
		FunctionSymbol function,
		BoundExpression receiver,
		BoundExpression argument,
		TextSpan span,
		IReadOnlyDictionary<string, KernType>? bindings = null)
	{
		if (function.ReturnType is null)
			return null;

		var target = function.Parameters[0].Type;
		var returnType = function.ReturnType;
		if (bindings is { Count: > 0 })
		{
			target = TypeSubstitution.Apply(target, bindings);
			returnType = TypeSubstitution.Apply(returnType, bindings);
		}

		if (!Conversions.TryClassify(argument.Type, target, out var conversion))
			return null;

		var converted = ConvertWithConversion(argument, target, conversion, argument.Span);
		// Q30: операторы класса диспатчатся по типу получателя; расширения остаются
		// статическими (IsVirtualCall отсекает IsExtension), unit-типы — тоже.
		return new BoundCallExpression(function, [converted], CallResultType(function, returnType), span, receiver)
		{
			Virtual = IsVirtualCall(receiver, function),
			TypeArguments = bindings is { Count: > 0 }
				? [.. function.GenericParameters.Select(parameter =>
					bindings.TryGetValue(parameter.Name, out KernType? inferred) ? inferred : parameter)]
				: [],
		};
	}
}
