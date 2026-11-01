using Kern.Compiler.Semantics.Attributes;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// B6b1/B8a/B8c: протоколы <c>hash</c>/<c>compare</c>/<c>clone</c> (spec §9, Q23/Q24 — вариант 1).
/// Прямой вызов <c>x.hash()</c>/<c>x.compare(y)</c>/<c>x.clone()</c> и упорядочивание
/// <c>&lt;</c>/<c>&lt;=</c>/<c>&gt;</c>/<c>&gt;=</c> работают через объявленный оператор типа,
/// а без него — через структурный хэш/сравнение VM (<c>ValueHash</c>/<c>ValueCompare</c>) для
/// <c>unit</c> с подходящими полями и через хэш по ссылке для <c>type</c>.
/// <c>#[derive(hash, compare)]</c> (B8a) включают у <c>type</c> полевое поведение
/// (<c>ValueHashFields</c>/<c>ValueCompareFields</c>), <c>#[derive(clone)]</c> (B8c) —
/// неглубокую копию (<c>CloneObject</c>). Упорядочивание произвольного <c>type</c> без
/// оператора и без derive — ошибка.
/// </summary>
public sealed partial class Binder
{
	private const string HashOperator = "hash";
	private const string CompareOperator = "compare";
	private const string CloneOperator = "clone";
	private const string ToStrOperator = "to_str";
	private const string ToDebugOperator = "to_debug";

	/// <summary>
	/// Вызывается из <see cref="BindMemberCall"/> после неудачи обычных членов и нативных
	/// методов, перед KRN2103. <c>null</c> — это не протокол, диагностик не добавлено.
	/// </summary>
	private BoundExpression? TryBindProtocolMember(
		BoundExpression receiver,
		string name,
		IReadOnlyList<CallArgumentNode> arguments,
		TextSpan span)
	{
		if (name == HashOperator && arguments.Count == 0)
			return BindHashProtocol(receiver, span);
		if (name == CompareOperator && arguments.Count == 1)
			return BindCompareProtocol(receiver, arguments[0], span);
		if (name == CloneOperator && arguments.Count == 0)
			return BindCloneProtocol(receiver, span);
		// `x.to_str()` (spec §3: `i32 n => n.to_str()`) — универсальный строковый вид значения.
		if (name == ToStrOperator && arguments.Count == 0 && FormatFunction(1) is { } format)
			return new BoundCallExpression(format, [receiver], PrimitiveType.Str, span);
		// `x.to_debug()` (Q25/B8b) — отладочное представление, верхнеуровневые str/char в кавычках.
		if (name == ToDebugOperator && arguments.Count == 0 && NativeGlobalFunction("to_debug", 1) is { } debug)
			return new BoundCallExpression(debug, [receiver], PrimitiveType.Str, span);
		return null;
	}

	/// <summary>
	/// <c>x.hash() -&gt; u64</c>: объявленный <c>op hash()</c> либо структурный хэш unit'а
	/// (только если все его поля хэшируемы) либо хэш по ссылке для <c>type</c>. У <c>type</c>
	/// с <c>#[derive(hash)]</c> — структурный хэш по полям (B8a).
	/// </summary>
	private BoundExpression? BindHashProtocol(BoundExpression receiver, TextSpan span)
	{
		var owner = OwnerOf(receiver.Type);
		if (owner is not null)
		{
			foreach (var function in owner.Members.Operators)
			{
				if (function.OpSymbol != HashOperator || function.Parameters.Count != 0)
					continue;
				if (function.ReturnType is null || !function.ReturnType.Equals(PrimitiveType.U64))
				{
					Report(
						"KRN2101",
						$"Оператор 'hash' в типе '{receiver.Type}' должен возвращать u64, а не '{TypeName(function.ReturnType)}'",
						span);
					return new BoundErrorExpression(span);
				}

				return new BoundCallExpression(function, [], function.ReturnType, span, receiver)
				{
					Virtual = IsVirtualCall(receiver, function),
				};
			}
		}

		if (receiver.Type is TupleType)
			return new BoundHashExpression(receiver, PrimitiveType.U64, span);

		if (receiver.Type is UnitType unit)
		{
			if (!IsHashableKey(unit))
			{
				Report("KRN2101", HashabilityMessage(unit), span);
				return new BoundErrorExpression(span);
			}

			return new BoundHashExpression(receiver, PrimitiveType.U64, span);
		}

		// spec §9: у `type` без derive хэш по ссылке (идентичность объекта), поля не читаются.
		// B8a: #[derive(hash)] переключает на структурный хэш по полям.
		if (receiver.Type is ClassType @class)
		{
			var structural = DeriveCatalog.Has(@class.Symbol.Declaration, DerivedProtocol.Hash);
			return new BoundHashExpression(receiver, PrimitiveType.U64, span, Structural: structural);
		}

		return null;
	}

	/// <summary>
	/// <c>x.compare(y) -&gt; i32</c>: объявленный <c>op compare(T)</c> либо структурное сравнение
	/// сравнимого unit'а. У <c>type</c> с <c>#[derive(compare)]</c> — полевое сравнение (B8a);
	/// без оператора и derive порядок не определён (Q24) — KRN2101.
	/// </summary>
	private BoundExpression? BindCompareProtocol(BoundExpression receiver, CallArgumentNode argument, TextSpan span)
	{
		var owner = OwnerOf(receiver.Type);
		List<FunctionSymbol> candidates = owner is null ? [] : CompareOperators(owner);
		if (candidates.Count == 0 && receiver.Type is not (UnitType or ClassType or TupleType))
			return null;

		var bound = BindExpression(argument.Value);
		if (IsError(bound.Type))
			return new BoundErrorExpression(span);

		if (candidates.Count > 0)
			return BindDeclaredCompareCall(candidates, receiver, bound, span);

		if (receiver.Type is TupleType)
		{
			var convertedTuple = Convert(bound, receiver.Type, bound.Span);
			if (IsError(convertedTuple.Type))
				return new BoundErrorExpression(span);
			return new BoundCompareExpression(receiver, convertedTuple, PrimitiveType.I32, span);
		}

		if (receiver.Type is UnitType unit)
		{
			var converted = Convert(bound, unit, bound.Span);
			if (IsError(converted.Type))
				return new BoundErrorExpression(span);
			if (!IsComparableType(unit))
			{
				Report(
					"KRN2101",
					$"Тип '{unit}' не поддерживает compare: все поля должны быть сравнимыми",
					span);
				return new BoundErrorExpression(span);
			}

			return new BoundCompareExpression(receiver, converted, PrimitiveType.I32, span);
		}

		if (receiver.Type is ClassType @class &&
			DeriveCatalog.Has(@class.Symbol.Declaration, DerivedProtocol.Compare))
		{
			var converted = Convert(bound, @class, bound.Span);
			if (IsError(converted.Type))
				return new BoundErrorExpression(span);
			return new BoundCompareExpression(receiver, converted, PrimitiveType.I32, span, Structural: true);
		}

		Report(
			"KRN2101",
			$"Для типа '{receiver.Type}' не определено упорядочивание: нужен op compare(...) -> i32",
			span);
		return new BoundErrorExpression(span);
	}

	/// <summary>
	/// B6b1: упорядочивание пользовательских типов в <c>BindComparison</c> (только при равных
	/// типах операндов). <c>null</c> — операнды не unit/type, решение принимает обычный путь.
	/// </summary>
	private BoundExpression? TryBindOrderedComparison(
		string symbol,
		BoundExpression left,
		BoundExpression right,
		TextSpan span)
	{
		if (left.Type is not (UnitType or ClassType or TupleType))
			return null;

		if (left.Type is TupleType tuple)
		{
			if (!IsComparableType(tuple))
			{
				Report("KRN2101", $"Тип '{tuple}' не поддерживает compare: элементы должны быть сравнимыми", span);
				return new BoundErrorExpression(span);
			}

			return new BoundBinaryExpression(
				new BoundCompareExpression(left, right, PrimitiveType.I32, span),
				symbol,
				ZeroI32(span),
				PrimitiveType.Bool,
				span);
		}

		var owner = OwnerOf(left.Type);
		List<FunctionSymbol> candidates = owner is null ? [] : CompareOperators(owner);
		BoundExpression? compared = null;
		if (candidates.Count > 0)
		{
			compared = BindDeclaredCompareCall(candidates, left, right, span);
			if (IsError(compared.Type))
				return compared;
		}
		else if (left.Type is UnitType unit && IsComparableType(unit))
		{
			compared = new BoundCompareExpression(left, right, PrimitiveType.I32, span);
		}
		else if (left.Type is ClassType @class &&
			DeriveCatalog.Has(@class.Symbol.Declaration, DerivedProtocol.Compare))
		{
			compared = new BoundCompareExpression(left, right, PrimitiveType.I32, span, Structural: true);
		}

		if (compared is null)
		{
			Report(
				"KRN2101",
				$"Оператор '{symbol}' не применим к типу '{left.Type}': нужен op compare(...) -> i32 или сравнимое unit",
				span);
			return new BoundErrorExpression(span);
		}

		return new BoundBinaryExpression(compared, symbol, ZeroI32(span), PrimitiveType.Bool, span);
	}

	/// <summary>
	/// B8c: <c>x.clone()</c> — объявленный <c>op clone() -&gt; T</c> либо у <c>type</c> с
	/// <c>#[derive(clone)]</c> неглубокая копия объекта (<c>CloneObject</c>). У
	/// <c>unit</c>/<c>unit!</c> значение и так копируется при присваивании, поэтому
	/// <c>clone()</c> возвращает само значение (derive у unit — no-op).
	/// </summary>
	private BoundExpression? BindCloneProtocol(BoundExpression receiver, TextSpan span)
	{
		var owner = OwnerOf(receiver.Type);
		if (owner is not null)
		{
			foreach (var function in owner.Members.Operators)
			{
				if (function.OpSymbol != CloneOperator || function.Parameters.Count != 0)
					continue;
				if (function.ReturnType is null || !function.ReturnType.Equals(receiver.Type))
				{
					Report(
						"KRN2101",
						$"Оператор 'clone' в типе '{receiver.Type}' должен возвращать '{receiver.Type}', а не '{TypeName(function.ReturnType)}'",
						span);
					return new BoundErrorExpression(span);
				}

				return new BoundCallExpression(function, [], function.ReturnType, span, receiver)
				{
					Virtual = IsVirtualCall(receiver, function),
				};
			}
		}

		if (receiver.Type is UnitType)
			return receiver;

		if (receiver.Type is ClassType @class)
		{
			if (DeriveCatalog.Has(@class.Symbol.Declaration, DerivedProtocol.Clone))
				return new BoundCloneExpression(receiver, @class, span);

			Report(
				"KRN2101",
				$"Для типа '{receiver.Type}' не определен clone: нужен op clone() -> {receiver.Type} или #[derive(clone)]",
				span);
			return new BoundErrorExpression(span);
		}

		return null;
	}

	/// <summary>
	/// Q23: объявленный <c>op ==</c>/<c>op !=</c> типа важнее структурного/ссылочного
	/// сравнения. <c>!=</c> без своего оператора вызывает <c>op ==</c> и инвертирует результат.
	/// </summary>
	private BoundExpression? TryBindEqualityOperator(
		string symbol,
		BoundExpression left,
		BoundExpression right,
		TextSpan span)
	{
		TypeSymbol? owner = OwnerOf(left.Type);
		if (owner is null)
			return null;

		var candidates = owner.Members.Operators
			.Where(function => function.OpSymbol == symbol && function.Parameters.Count == 1)
			.ToList();
		var invert = false;
		if (candidates.Count == 0 && symbol == "!=")
		{
			candidates = owner.Members.Operators
				.Where(function => function.OpSymbol == "==" && function.Parameters.Count == 1)
				.ToList();
			invert = candidates.Count > 0;
		}

		if (candidates.Count == 0)
			return null;

		FunctionSymbol selected = SelectCompareOperator(candidates, right.Type);
		if (selected.ReturnType is null || !selected.ReturnType.Equals(PrimitiveType.Bool))
		{
			Report(
				"KRN2101",
				$"Оператор '{selected.OpSymbol}' в типе '{left.Type}' должен возвращать bool, а не '{TypeName(selected.ReturnType)}'",
				span);
			return new BoundErrorExpression(span);
		}

		BoundExpression argument = Convert(right, selected.Parameters[0].Type, right.Span);
		if (IsError(argument.Type))
			return new BoundErrorExpression(span);

		var call = new BoundCallExpression(selected, [argument], PrimitiveType.Bool, span, left)
		{
			Virtual = IsVirtualCall(left, selected),
		};
		return invert
			? new BoundUnaryExpression("!", call, PrimitiveType.Bool, span)
			: call;
	}

	/// <summary>Вызов объявленного <c>op compare</c>; требует возврата <c>i32</c>.</summary>
	private BoundExpression BindDeclaredCompareCall(
		IReadOnlyList<FunctionSymbol> candidates,
		BoundExpression receiver,
		BoundExpression argument,
		TextSpan span)
	{
		var selected = SelectCompareOperator(candidates, argument.Type);
		if (selected.ReturnType is null || !selected.ReturnType.Equals(PrimitiveType.I32))
		{
			Report(
				"KRN2101",
				$"Оператор 'compare' в типе '{receiver.Type}' должен возвращать i32, а не '{TypeName(selected.ReturnType)}'",
				span);
			return new BoundErrorExpression(span);
		}

		var converted = Convert(argument, selected.Parameters[0].Type, argument.Span);
		if (IsError(converted.Type))
			return new BoundErrorExpression(span);

		return new BoundCallExpression(selected, [converted], PrimitiveType.I32, span, receiver)
		{
			// Q30: пользовательский op compare класса диспатчится по типу получателя.
			Virtual = IsVirtualCall(receiver, selected),
		};
	}

	/// <summary>Все объявленные <c>op compare</c> типа с одним параметром.</summary>
	private static List<FunctionSymbol> CompareOperators(TypeSymbol owner)
	{
		var result = new List<FunctionSymbol>();
		foreach (var function in owner.Members.Operators)
			if (function.OpSymbol == CompareOperator && function.Parameters.Count == 1)
				result.Add(function);
		return result;
	}

	/// <summary>Точное совпадение типа аргумента, затем первое конвертируемое, иначе первый.</summary>
	private static FunctionSymbol SelectCompareOperator(
		IReadOnlyList<FunctionSymbol> candidates,
		KernType argumentType)
	{
		foreach (var candidate in candidates)
			if (candidate.Parameters[0].Type.Equals(argumentType))
				return candidate;

		foreach (var candidate in candidates)
			if (!IsError(argumentType) && Conversions.TryClassify(argumentType, candidate.Parameters[0].Type, out _))
				return candidate;

		return candidates[0];
	}

	/// <summary>
	/// B6b1: тип поддерживает структурное сравнение — числа, <c>bool</c>, <c>char</c>, <c>str</c>
	/// и unit'ы, у которых каждое нестатическое не-backing поле сравнимо (рекурсивно; цикл
	/// считается сравнимым). <c>T?</c>, коллекции, классы, функции, кортежи, union и <c>object</c>
	/// — нет (spec §9, Q24).
	/// </summary>
	private static bool IsComparableType(KernType type) => IsComparableType(type, []);

	private static bool IsComparableType(KernType type, HashSet<TypeSymbol> visiting) => type switch
	{
		PrimitiveType primitive => NumericInfo.TryGet(primitive.Primitive, out _) ||
			primitive.Primitive is PrimitiveKind.Bool or PrimitiveKind.Char or PrimitiveKind.Str,
		UnitType unit => IsComparableUnit(unit, visiting),
		TupleType tuple => tuple.Elements.All(element => IsComparableType(element.Type, visiting)),
		_ => false,
	};

	private static bool IsComparableUnit(UnitType unit, HashSet<TypeSymbol> visiting)
	{
		if (!visiting.Add(unit.Symbol))
			return true;

		foreach (FieldSymbol field in unit.Symbol.Members.Fields)
		{
			if (field.IsStatic || field.IsBackingField)
				continue;
			if (!IsComparableType(field.Type, visiting))
				return false;
		}

		return true;
	}

	/// <summary>Нулевой литерал <c>i32</c> для сравнения результата <c>compare</c>.</summary>
	private static BoundLiteralExpression ZeroI32(TextSpan span) =>
		new(0, NumericLiteralValue.FromInteger(0), PrimitiveType.I32, span);

	private static string TypeName(KernType? type) => type?.ToString() ?? "void";
}
