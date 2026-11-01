using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Операторы вхождения <c>a in b</c> / <c>a !in b</c> (spec §3, §16): для <c>list</c>/<c>array</c>
/// вызывается нативный <c>contains</c>, для <c>dict</c> — <c>contains_key</c>, для <c>str</c> —
/// подстрока (<c>str</c>) или символ (<c>char</c> через <c>to_str</c>). Результат — <c>bool</c>.
/// </summary>
public sealed partial class Binder
{
	private BoundExpression BindMembership(
		string symbol,
		BoundExpression left,
		BoundExpression right,
		TextSpan span)
	{
		BoundExpression? membership = right.Type switch
		{
			BuiltinGenericType { BuiltinKind: BuiltinGenericKind.List or BuiltinGenericKind.Array } list
				when list.Arguments.Count == 1 =>
				CallContains(right, "contains", left, list.Arguments[0], span),
			BuiltinGenericType { BuiltinKind: BuiltinGenericKind.Dict } dictionary
				when dictionary.Arguments.Count == 2 =>
				CallContains(right, "contains_key", left, dictionary.Arguments[0], span),
			BuiltinGenericType { BuiltinKind: BuiltinGenericKind.Set } set
				when set.Arguments.Count == 1 =>
				CallContains(right, "contains", left, set.Arguments[0], span),
			PrimitiveType { Primitive: PrimitiveKind.Str } =>
				BindStringMembership(right, left, span),
			_ => null,
		};

		if (membership is null)
		{
			Report(
				"KRN2101",
				$"Оператор '{symbol}' не применим к '{left.Type}' и '{right.Type}'",
				span);
			return new BoundErrorExpression(span);
		}

		if (IsError(membership.Type))
			return membership;

		return symbol == "in"
			? membership
			: new BoundUnaryExpression("!", membership, PrimitiveType.Bool, span);
	}

	/// <summary>Вхождение в <c>str</c>: подстрока для <c>str</c>, символ для <c>char</c>.</summary>
	private BoundExpression? BindStringMembership(
		BoundExpression text,
		BoundExpression item,
		TextSpan span)
	{
		if (item.Type.Equals(PrimitiveType.Str))
			return CallContains(text, "contains", item, PrimitiveType.Str, span);

		if (!item.Type.Equals(PrimitiveType.Char))
			return null;

		FunctionSymbol? toStr = null;
		foreach (var native in NativeMembers(item.Type, "to_str"))
		{
			toStr = native;
			break;
		}

		if (toStr is null)
			return null;

		var symbol = new BoundCallExpression(toStr, [], PrimitiveType.Str, span, item);
		return CallContains(text, "contains", symbol, PrimitiveType.Str, span);
	}

	/// <summary>Вызов нативного члена-предиката с приведением аргумента к типу элемента/ключа.</summary>
	private BoundExpression? CallContains(
		BoundExpression receiver,
		string name,
		BoundExpression item,
		KernType itemType,
		TextSpan span)
	{
		FunctionSymbol? function = null;
		KernType target = itemType;
		foreach (var native in NativeMembers(receiver.Type, name))
		{
			if (native.Parameters.Count == 0)
				continue;
			function = native;
			// Приёмник у нативных членов может входить в список параметров — берём последний;
			// generic-параметр встроенного типа заменяем типом элемента/ключа. `object` —
			// заглушка моста для элемента контейнера (`set<T>.contains`), тоже заменяется.
			var parameter = native.Parameters[^1].Type;
			target = parameter is TypeParameterType or PrimitiveType { Primitive: PrimitiveKind.Object }
				? itemType
				: parameter;
			break;
		}

		if (function is null)
			return null;

		var converted = Convert(item, target, span);
		if (IsError(converted.Type))
			return new BoundErrorExpression(span);

		return new BoundCallExpression(
			function,
			[converted],
			PrimitiveType.Bool,
			span,
			receiver);
	}
}
