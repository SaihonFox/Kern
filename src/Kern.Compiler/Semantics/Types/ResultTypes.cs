using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Встроенные варианты <c>result&lt;T, E&gt;</c>: unit-типы <c>Ok&lt;T&gt;</c> и <c>Err&lt;E&gt;</c>.
/// <c>result&lt;T, E&gt;</c> разрешается в union из них, поэтому конструкторы, деструктуризация,
/// pattern matching и покрытие работают существующим механизмом unit'ов.
/// </summary>
public static class ResultTypes
{
	public const string ResultName = "result";
	public const string OkName = "Ok";
	public const string ErrName = "Err";
	public const string OkBaseName = "ok";
	public const string ErrBaseName = "error";

	public static bool IsResultVariant(TypeSymbol symbol) =>
		ReferenceEquals(symbol, Ok) || ReferenceEquals(symbol, Err);

	/// <summary>Тип — объединение встроенных вариантов <c>Ok</c>/<c>Err</c> (spec §16).</summary>
	public static bool IsResultType(KernType type) =>
		type is UnionType union && union.Alternatives.All(
			alternative => alternative is UnitType unit && IsResultVariant(unit.Symbol));

	/// <summary>Объединение <c>Ok&lt;T&gt;</c>/<c>Err&lt;E&gt;</c> для <c>result&lt;T, E&gt;</c>.</summary>
	public static KernType CreateType(KernType ok, KernType error) =>
		UnionType.Create(
		[
			new UnitType(Ok, [ok], false),
			new UnitType(Err, [error], false)
		]);

	/// <summary>Текст типа для диагностик: union из Ok/Err печатается как <c>result&lt;T, E&gt;</c>.</summary>
	public static bool TryDescribe(KernType type, out string text)
	{
		text = string.Empty;
		if (type is not UnionType { Alternatives.Count: 2 } union)
			return false;

		UnitType? ok = null;
		UnitType? error = null;
		foreach (var alternative in union.Alternatives)
		{
			if (alternative is not UnitType { Arguments.Count: 1 } unit)
				return false;
			if (ReferenceEquals(unit.Symbol, Ok) && ok is null)
				ok = unit;
			else if (ReferenceEquals(unit.Symbol, Err) && error is null)
				error = unit;
			else
				return false;
		}

		if (ok is null || error is null)
			return false;
		text = $"result<{ok.Arguments[0]}, {error.Arguments[0]}>";
		return true;
	}

	public static FieldSymbol FieldOf(TypeSymbol symbol) =>
		ReferenceEquals(symbol, Ok) ? OkField : ErrField;

	private static readonly TypeParameterType OkParameter = new("T", []);
	private static readonly TypeParameterType ErrParameter = new("E", []);

	private static readonly FieldSymbol OkField = new(
		"value",
		OkParameter,
		IsRequired: false,
		IsStatic: false,
		Accessibility.Public,
		Initializer: null,
		IsConst: false);

	private static readonly FieldSymbol ErrField = new(
		"error",
		ErrParameter,
		IsRequired: false,
		IsStatic: false,
		Accessibility.Public,
		Initializer: null,
		IsConst: false);

	public static TypeSymbol Ok { get; } = CreateVariant(OkName, "T", "value");
	public static TypeSymbol Err { get; } = CreateVariant(ErrName, "E", "error");

	/// <summary>
	/// Маркерный базовый тип <c>ok</c> (spec §16): абстрактный, без членов и generic-параметров.
	/// Пользовательские варианты наследуют его: <c>type SomeOK[ok] { ... }</c>.
	/// </summary>
	public static TypeSymbol OkMarker { get; } = CreateMarker(OkBaseName);

	/// <summary>Маркерный базовый тип <c>error</c> (spec §16).</summary>
	public static TypeSymbol ErrMarker { get; } = CreateMarker(ErrBaseName);

	private static TypeSymbol CreateMarker(string name) => new()
	{
		Name = name,
		Kind = SymbolKind.AbstractClass,
		IsAbstract = true
	};

	private static TypeSymbol CreateVariant(string name, string parameterName, string fieldName)
	{
		var parameter = parameterName == "T" ? OkParameter : ErrParameter;
		var field = fieldName == "value" ? OkField : ErrField;
		return new TypeSymbol
		{
			Name = name,
			Kind = SymbolKind.Unit,
			GenericParameterNames = [parameterName],
			GenericParameters = [parameter],
			Members = MemberTable.ForFields([field])
		};
	}
}
