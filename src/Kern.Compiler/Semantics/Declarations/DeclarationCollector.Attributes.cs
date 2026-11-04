using Kern.Compiler.Semantics.Attributes;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Declarations;

public sealed partial class DeclarationCollector
{
	/// <summary>
	/// KRN2307/KRN2101/KRN2106: неизвестные имена, цели и аргументы атрибутов (spec §7).
	/// Обходятся атрибуты модуля, объявления верхнего уровня и вложенные члены типов/unit;
	/// тела функций не обходятся. <c>#[derive]</c> дополнительно проверяет протоколы.
	/// </summary>
	private void ValidateAttributes(CompilationUnit unit)
	{
		ValidateAttributeList(unit.Attributes, AttributeTarget.Module, "модулю");
		foreach (var declaration in unit.Declarations)
			ValidateDeclarationAttributes(declaration);
	}

	private void ValidateDeclarationAttributes(SyntaxNode declaration)
	{
		var attributes = ConditionalCompilation.AttributesOf(declaration);
		if (!ValidateConditionalAttributes(attributes) || !ConditionalCompilation.IsIncluded(attributes))
			return;

		if (AttributeValidation.TargetOf(declaration) is { } target)
			ValidateAttributeList(attributes, target, AttributeValidation.DescribeTarget(declaration));

		switch (declaration)
		{
			case TypeDeclaration type:
				ValidateTypeDerives(type);
				foreach (var member in type.DeclaredMembers)
					ValidateDeclarationAttributes(member);
				break;
			case UnitDeclaration unitType:
				ValidateDeriveArguments(DeriveCatalog.Attributes(unitType.DeclaredAttributes));
				foreach (var member in unitType.DeclaredMembers)
					ValidateDeclarationAttributes(member);
				break;
		}
	}

	/// <summary>KRN2101 для каждого некорректного <c>#[if]</c>; ложь — объявление исключено.</summary>
	private bool ValidateConditionalAttributes(IReadOnlyList<AttributeNode> attributes)
	{
		var valid = true;
		foreach (var attribute in attributes)
		{
			if (!string.Equals(attribute.Name, "if", StringComparison.Ordinal))
				continue;
			if (ConditionalCompilation.TryEvaluate(attribute, out string? error) || error is null)
				continue;
			Report("KRN2101", error, attribute.Span);
			valid = false;
		}
		return valid;
	}

	/// <summary>KRN2307 для неизвестных имён, KRN2101 для недопустимой цели или аргументов.</summary>
	private void ValidateAttributeList(
		IReadOnlyList<AttributeNode> attributes,
		AttributeTarget target,
		string targetDescription)
	{
		foreach (var attribute in attributes)
		{
			if (!AttributeCatalog.IsKnown(attribute.Name))
			{
				Report(
					"KRN2307",
					$"Неизвестный атрибут '#[{attribute.Name}]'; известны: {string.Join(", ", AttributeCatalog.KnownNames)}",
					attribute.Span,
					DiagnosticSeverity.Warning);
				continue;
			}

			if (!AttributeValidation.TryValidate(attribute, target, targetDescription, out string? error) &&
				error is not null)
				Report("KRN2101", error, attribute.Span);
		}
	}

	/// <summary>
	/// B8a: <c>#[derive]</c> допустим только у <c>type</c>, который не union-алиас и не
	/// статический тип. Остальные цели — KRN2101 на самом атрибуте.
	/// </summary>
	private void ValidateTypeDerives(TypeDeclaration type)
	{
		var attributes = DeriveCatalog.Attributes(type.DeclaredAttributes);
		if (attributes.Count == 0)
			return;

		if (type.IsStatic)
		{
			ReportDeriveTarget(attributes, $"статическому типу '{type.Name}'");
			return;
		}

		if (type.DeclaredUnionVariants.Count > 0)
		{
			ReportDeriveTarget(attributes, $"union-алиасу '{type.Name}'");
			return;
		}

		ValidateDeriveArguments(attributes);
	}

	private void ReportDeriveTarget(IReadOnlyList<AttributeNode> attributes, string target)
	{
		foreach (var attribute in attributes)
			Report("KRN2101", $"#[derive] нельзя применять к {target}: атрибут допустим только у type или unit", attribute.Span);
	}

	/// <summary>
	/// B8a: имена аргументов и повторы. Пустой <c>#[derive]</c> и неизвестный протокол —
	/// KRN2101, повтор протокола (в одном атрибуте или в нескольких) — KRN2106.
	/// </summary>
	private void ValidateDeriveArguments(IReadOnlyList<AttributeNode> attributes)
	{
		var seen = new HashSet<DerivedProtocol>();
		foreach (var attribute in attributes)
		{
			if (attribute.Arguments.Count == 0)
			{
				Report(
					"KRN2101",
					$"#[derive] без протоколов: укажите один или несколько ({DeriveCatalog.KnownText})",
					attribute.Span);
				continue;
			}

			foreach (var argument in attribute.Arguments)
			{
				if (!DeriveCatalog.TryParse(argument, out var protocol))
				{
					Report(
						"KRN2101",
						$"Неизвестный протокол '{argument}' в #[derive]; доступны: {DeriveCatalog.KnownText}",
						attribute.Span);
					continue;
				}

				if (!seen.Add(protocol))
				{
					Report(
						"KRN2106",
						$"Протокол '{argument}' уже указан в #[derive]",
						attribute.Span);
				}
			}
		}
	}
}

