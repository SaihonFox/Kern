using Kern.Bytecode;
using Kern.Compiler.Semantics.Attributes;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>
	/// KRN2306: вызов функции, помеченной <c>#[deprecated]</c>. Предупреждение выдаётся на
	/// каждый успешно разрешённый вызов (не на объявление); первый аргумент атрибута, если он
	/// есть, добавляется к сообщению как причина/замена. Атрибуты читаются с символа
	/// (<see cref="FunctionSymbol.Attributes"/>), который заполняет сборщик объявлений.
	/// </summary>
	private void WarnDeprecated(FunctionSymbol function, TextSpan span)
	{
		if (AttributeCatalog.Find(function.Attributes, "deprecated") is not { } attribute)
			return;
		Warn("KRN2306", $"Функция '{function.Name}' устарела (#[deprecated]){DeprecatedReason(attribute)}", span);
	}

	/// <summary>KRN2306 для создания значения типа, помеченного <c>#[deprecated]</c>.</summary>
	private void WarnDeprecatedType(TypeSymbol type, TextSpan span)
	{
		if (AttributeCatalog.Find(type.Attributes, "deprecated") is not { } attribute)
			return;
		Warn("KRN2306", $"Тип '{type.Name}' устарел (#[deprecated]){DeprecatedReason(attribute)}", span);
	}

	/// <summary>
	/// Потерянное значение помечено <c>#[must_use]</c>: у вызова смотрим на атрибуты функции,
	/// у значения — на атрибуты его типа (unit/type, включая создание объекта).
	/// </summary>
	private static bool IsMustUse(BoundExpression expression)
	{
		if (expression is BoundCallExpression call && AttributeCatalog.Has(call.Function.Attributes, "must_use"))
			return true;
		return expression.Type switch
		{
			UnitType unit => AttributeCatalog.Has(unit.Symbol.Attributes, "must_use"),
			ClassType type => AttributeCatalog.Has(type.Symbol.Attributes, "must_use"),
			_ => false
		};
	}

	/// <summary>Причина из первого аргумента атрибута; строка выводится без кавычек.</summary>
	private static string DeprecatedReason(AttributeUse attribute)
	{
		if (attribute.Args.Count == 0)
			return "";
		var argument = attribute.Args[0];
		var text = argument.Kind == KConstKind.Str ? argument.AsStr : argument.Describe();
		return $": {text}";
	}
}
