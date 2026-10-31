using Kern.Compiler.Semantics.Attributes;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

public sealed partial class Binder
{
	/// <summary>
	/// KRN2306: вызов функции, помеченной <c>#[deprecated]</c>. Предупреждение выдаётся на
	/// каждый успешно разрешённый вызов (не на объявление); первый аргумент атрибута, если он
	/// есть, добавляется к сообщению как причина/замена.
	/// </summary>
	private void WarnDeprecated(FunctionSymbol function, TextSpan span)
	{
		if (AttributeCatalog.Find(function.Declaration, "deprecated") is not { } attribute)
			return;
		Warn("KRN2306", $"Функция '{function.Name}' устарела (#[deprecated]){DeprecatedReason(attribute)}", span);
	}

	/// <summary>KRN2306 для создания значения типа, помеченного <c>#[deprecated]</c>.</summary>
	private void WarnDeprecatedType(TypeSymbol type, TextSpan span)
	{
		if (AttributeCatalog.Find(type.Declaration, "deprecated") is not { } attribute)
			return;
		Warn("KRN2306", $"Тип '{type.Name}' устарел (#[deprecated]){DeprecatedReason(attribute)}", span);
	}

	/// <summary>
	/// Потерянное значение помечено <c>#[must_use]</c>: у вызова смотрим на объявление функции,
	/// у значения — на объявление его типа (unit/type, включая создание объекта).
	/// </summary>
	private static bool IsMustUse(BoundExpression expression)
	{
		if (expression is BoundCallExpression call && AttributeCatalog.Has(call.Function.Declaration, "must_use"))
			return true;
		return expression.Type switch
		{
			UnitType unit => AttributeCatalog.Has(unit.Symbol.Declaration, "must_use"),
			ClassType type => AttributeCatalog.Has(type.Symbol.Declaration, "must_use"),
			_ => false
		};
	}

	/// <summary>Причина из первого аргумента атрибута; кавычки строкового литерала снимаются.</summary>
	private static string DeprecatedReason(AttributeNode attribute)
	{
		if (attribute.Arguments.Count == 0)
			return "";
		var text = attribute.Arguments[0].Trim();
		if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
			text = text[1..^1];
		return $": {text}";
	}
}
