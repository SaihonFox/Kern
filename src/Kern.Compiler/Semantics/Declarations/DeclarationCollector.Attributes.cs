using Kern.Compiler.Semantics.Attributes;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Declarations;

public sealed partial class DeclarationCollector
{
	/// <summary>
	/// KRN2307/KRN2101/KRN2106: неизвестный атрибут и проверка <c>#[derive(...)]</c> (B7/B8a,
	/// spec §7). Обходятся объявления верхнего уровня, их атрибуты и вложенные типы в
	/// <see cref="TypeDeclaration.DeclaredMembers"/>/<see cref="UnitDeclaration.DeclaredMembers"/>;
	/// тела функций не обходятся.
	/// </summary>
	private void ValidateAttributes(CompilationUnit unit)
	{
		foreach (var declaration in unit.Declarations)
			ValidateDeclarationAttributes(declaration);
	}

	private void ValidateDeclarationAttributes(SyntaxNode declaration)
	{
		switch (declaration)
		{
			case TypeDeclaration type:
				ReportUnknownAttributes(type.DeclaredAttributes);
				ValidateTypeDerives(type);
				foreach (var member in type.DeclaredMembers)
					ValidateDeclarationAttributes(member);
				break;
			case UnitDeclaration unitType:
				ReportUnknownAttributes(unitType.DeclaredAttributes);
				ValidateDeriveArguments(DeriveCatalog.Attributes(unitType.DeclaredAttributes));
				foreach (var member in unitType.DeclaredMembers)
					ValidateDeclarationAttributes(member);
				break;
			case FunctionDeclaration function:
				ReportUnknownAttributes(function.DeclaredAttributes);
				ValidateFunctionDerives(function);
				break;
			case FieldMemberNode field:
				ReportUnknownAttributes(field.DeclaredAttributes);
				break;
			case PropertyMemberNode property:
				ReportUnknownAttributes(property.DeclaredAttributes);
				break;
			case OperatorMemberNode @operator:
				ReportUnknownAttributes(@operator.DeclaredAttributes);
				break;
			case ConstructorMemberNode constructor:
				ReportUnknownAttributes(constructor.DeclaredAttributes);
				break;
			case ParentMethodSelectionNode selection:
				ReportUnknownAttributes(selection.DeclaredAttributes);
				break;
			case ParentFieldSelectionNode fieldSelection:
				ReportUnknownAttributes(fieldSelection.DeclaredAttributes);
				break;
			case GlobalFieldDeclaration global:
				ReportUnknownAttributes(global.DeclaredAttributes);
				break;
			case ExtensionOperatorDeclaration extensionOperator:
				ReportUnknownAttributes(extensionOperator.DeclaredAttributes);
				break;
			case ExtensionPropertyDeclaration extensionProperty:
				ReportUnknownAttributes(extensionProperty.DeclaredAttributes);
				break;
		}
	}

	private void ReportUnknownAttributes(IReadOnlyList<AttributeNode> attributes)
	{
		foreach (var attribute in attributes)
		{
			if (AttributeCatalog.IsKnown(attribute.Name))
				continue;
			Report(
				"KRN2307",
				$"Неизвестный атрибут '#[{attribute.Name}]'; известны: {string.Join(", ", AttributeCatalog.KnownNames)}",
				attribute.Span,
				DiagnosticSeverity.Warning);
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
			ReportDeriveTarget(attributes, $"статического типа '{type.Name}'");
			return;
		}

		if (type.DeclaredUnionVariants.Count > 0)
		{
			ReportDeriveTarget(attributes, $"union-алиаса '{type.Name}'");
			return;
		}

		ValidateDeriveArguments(attributes);
	}

	private void ValidateFunctionDerives(FunctionDeclaration function)
	{
		var attributes = DeriveCatalog.Attributes(function.DeclaredAttributes);
		if (attributes.Count == 0)
			return;

		ReportDeriveTarget(attributes, $"функции '{function.Name}'");
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

