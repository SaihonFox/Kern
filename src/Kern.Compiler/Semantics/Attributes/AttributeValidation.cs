using Kern.Compiler.Lexing;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Attributes;

/// <summary>Вид объявления для проверки целей атрибутов (spec §7).</summary>
public enum AttributeTarget
{
	Module,
	Type,
	Unit,
	Function,
	Field,
	Property,
	Operator,
	Constructor,
	ParentSelection,
	GlobalField,
	ExtensionOperator,
	ExtensionProperty,
	Use
}

/// <summary>
/// Проверка допустимых целей и аргументов встроенных атрибутов (spec §7). Неизвестные имена
/// не проверяются — для них сборщик объявлений выдаёт <c>KRN2307</c>. <c>#[derive]</c>
/// дополнительно проверяется <see cref="DeriveCatalog"/>, <c>#[if]</c> —
/// <see cref="ConditionalCompilation"/>, <c>#[needs]</c> — <see cref="CapabilityParser"/>;
/// здесь проверяются цель и форма их аргументов.
/// </summary>
public static class AttributeValidation
{
	/// <summary>Вид объявления для узла или null, если у узла нет атрибутов.</summary>
	public static AttributeTarget? TargetOf(SyntaxNode? node) => node switch
	{
		CompilationUnit => AttributeTarget.Module,
		TypeDeclaration => AttributeTarget.Type,
		UnitDeclaration => AttributeTarget.Unit,
		FunctionDeclaration => AttributeTarget.Function,
		FieldMemberNode => AttributeTarget.Field,
		PropertyMemberNode => AttributeTarget.Property,
		OperatorMemberNode => AttributeTarget.Operator,
		ConstructorMemberNode => AttributeTarget.Constructor,
		ParentMethodSelectionNode or ParentFieldSelectionNode => AttributeTarget.ParentSelection,
		GlobalFieldDeclaration => AttributeTarget.GlobalField,
		ExtensionOperatorDeclaration => AttributeTarget.ExtensionOperator,
		ExtensionPropertyDeclaration => AttributeTarget.ExtensionProperty,
		UseDeclaration => AttributeTarget.Use,
		_ => null,
	};

	/// <summary>Описание объявления в дательном падеже для текста диагностики.</summary>
	public static string DescribeTarget(SyntaxNode node) => node switch
	{
		CompilationUnit => "модулю",
		TypeDeclaration type => $"типу '{type.Name}'",
		UnitDeclaration unit => $"unit-типу '{unit.Name}'",
		FunctionDeclaration function => $"функции '{function.Name}'",
		FieldMemberNode field => $"полю '{field.Name}'",
		PropertyMemberNode property => $"свойству '{property.Name}'",
		OperatorMemberNode => "оператору",
		ConstructorMemberNode => "конструктору",
		ParentMethodSelectionNode selection => $"выбору родителя '{selection.Name}'",
		ParentFieldSelectionNode selection => $"выбору поля '{selection.Name}'",
		GlobalFieldDeclaration global => $"глобальному полю '{global.Name}'",
		ExtensionOperatorDeclaration => "расширению-оператору",
		ExtensionPropertyDeclaration property => $"extension-свойству '{property.Name}'",
		UseDeclaration => "use-объявлению",
		_ => "объявлению",
	};

	/// <summary>
	/// Проверить цель и аргументы атрибута. Возвращает ложь и русский текст ошибки для
	/// недопустимой цели или аргументов; неизвестные имена считаются допустимыми.
	/// </summary>
	public static bool TryValidate(
		AttributeNode attribute,
		AttributeTarget target,
		string targetDescription,
		out string? error)
	{
		error = null;
		if (!AttributeCatalog.IsKnown(attribute.Name))
			return true;

		if (string.Equals(attribute.Name, "module", StringComparison.Ordinal) &&
			target != AttributeTarget.Module)
		{
			error = "#[module] допустим только в начале файла или перед объявлением project";
			return false;
		}

		if (!IsAllowed(attribute.Name, target))
		{
			error = $"#[{attribute.Name}] нельзя применять к {targetDescription}: " +
				$"атрибут допустим только {AllowedText(attribute.Name)}";
			return false;
		}

		return TryValidateArguments(attribute, out error);
	}

	/// <summary>Строковый литерал с любым допустимым префиксом (<c>"…"</c>, <c>r"…"</c>, …).</summary>
	internal static bool IsStringLiteral(string token) =>
		StringLiteralText.TryReadHeader(token, out _, out _);

	private static bool IsAllowed(string name, AttributeTarget target) => name switch
	{
		"deprecated" or "must_use" or "cfg" or "if" or "allow" => true,
		"inline" or "no_inline" or "memo" or "needs" or "test" or "macro" or
			"get" or "post" or "put" or "patch" or "delete" => target == AttributeTarget.Function,
		"auth" or "roles" => target is AttributeTarget.Type or AttributeTarget.Function,
		"derive" => target is AttributeTarget.Type or AttributeTarget.Unit,
		"controller" or "singleton" or "transient" or "scoped" => target == AttributeTarget.Type,
		"inject" => target is AttributeTarget.Field or AttributeTarget.Property,
		"module" => target == AttributeTarget.Module,
		_ => true,
	};

	private static string AllowedText(string name) => name switch
	{
		"inline" or "no_inline" or "memo" or "needs" or "test" or "macro" or
			"get" or "post" or "put" or "patch" or "delete" => "у функции",
		"auth" or "roles" => "у type или функции",
		"derive" => "у type или unit",
		"controller" or "singleton" or "transient" or "scoped" => "у type",
		"inject" => "у поля или свойства",
		_ => "у этого объявления",
	};

	private static bool TryValidateArguments(AttributeNode attribute, out string? error)
	{
		error = null;
		switch (attribute.Name)
		{
			case "deprecated":
			case "cfg":
			case "controller":
			case "get":
			case "post":
			case "put":
			case "patch":
			case "delete":
			case "macro":
				return TryOptionalString(attribute, out error);
			case "must_use":
			case "inline":
			case "no_inline":
			case "memo":
			case "test":
			case "auth":
			case "singleton":
			case "transient":
			case "scoped":
				return TryNoArguments(attribute, out error);
			case "if":
				ConditionalCompilation.TryEvaluate(attribute, out error);
				return error is null;
			case "needs":
				return CapabilityParser.TryValidate(attribute, out error);
			case "allow":
				return TryAllow(attribute, out error);
			case "roles":
				return TryRoles(attribute, out error);
			case "inject":
				return TryInject(attribute, out error);
			case "module":
				return TryModule(attribute, out error);
			case "derive":
				// Имена протоколов, пустой список и повторы проверяет DeriveCatalog.
				return true;
			default:
				return true;
		}
	}

	private static bool TryOptionalString(AttributeNode attribute, out string? error)
	{
		error = null;
		if (attribute.Arguments.Count == 0)
			return true;
		if (attribute.Arguments.Count == 1 && IsStringLiteral(attribute.Arguments[0]))
			return true;

		error = $"#[{attribute.Name}] принимает не более одного строкового аргумента";
		return false;
	}

	private static bool TryNoArguments(AttributeNode attribute, out string? error)
	{
		error = null;
		if (attribute.Arguments.Count == 0)
			return true;

		error = $"#[{attribute.Name}] не принимает аргументов";
		return false;
	}

	private static bool TryAllow(AttributeNode attribute, out string? error)
	{
		error = null;
		if (attribute.Arguments.Count == 0)
		{
			error = "#[allow] требует хотя бы один код: #[allow(KRN2305)] или #[allow(warnings)]";
			return false;
		}

		foreach (string argument in attribute.Arguments)
		{
			if (argument is "warnings" or "all" || IsDiagnosticCode(argument))
				continue;
			error = $"Неизвестный код '{argument}' в #[allow]: ожидался KRNdddd, warnings или all";
			return false;
		}

		return true;
	}

	private static bool TryRoles(AttributeNode attribute, out string? error)
	{
		error = null;
		if (attribute.Arguments.Count == 0)
		{
			error = "#[roles] требует хотя бы одну роль: #[roles(\"admin\")]";
			return false;
		}

		foreach (string argument in attribute.Arguments)
		{
			if (IsStringLiteral(argument))
				continue;
			error = $"#[roles]: аргумент '{argument}' должен быть строковым литералом";
			return false;
		}

		return true;
	}

	private static bool TryInject(AttributeNode attribute, out string? error)
	{
		error = null;
		var arguments = attribute.Arguments;
		if (arguments.Count == 0)
			return true;
		if (arguments.Count == 1 && IsStringLiteral(arguments[0]))
			return true;
		if (arguments.Count == 3 &&
			arguments[0] == "optional" &&
			arguments[1] == ":" &&
			arguments[2] is "true" or "false")
			return true;

		error = "#[inject] принимает имя строкой или optional: true/false";
		return false;
	}

	private static bool TryModule(AttributeNode attribute, out string? error)
	{
		error = null;
		var arguments = attribute.Arguments;
		if (arguments.Count == 0)
			return true;
		if (arguments.Count % 3 != 0)
		{
			error = "#[module]: ожидались пары key = \"value\"";
			return false;
		}

		for (var index = 0; index < arguments.Count; index += 3)
		{
			if (IsIdentifier(arguments[index]) &&
				arguments[index + 1] == "=" &&
				IsStringLiteral(arguments[index + 2]))
				continue;
			error = "#[module]: ожидались пары key = \"value\"";
			return false;
		}

		return true;
	}

	private static bool IsDiagnosticCode(string text) =>
		text.Length == 7 &&
		text.StartsWith("KRN", StringComparison.Ordinal) &&
		text[3] is >= '0' and <= '9' &&
		text[4] is >= '0' and <= '9' &&
		text[5] is >= '0' and <= '9' &&
		text[6] is >= '0' and <= '9';

	private static bool IsIdentifier(string text)
	{
		if (text.Length == 0 || !(char.IsLetter(text[0]) || text[0] == '_'))
			return false;
		foreach (char symbol in text)
		{
			if (!(char.IsLetterOrDigit(symbol) || symbol == '_'))
				return false;
		}
		return true;
	}
}
