using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Attributes;

/// <summary>Протокол, который можно получить через <c>#[derive(...)]</c> (B8a, spec §7/§16).</summary>
public enum DerivedProtocol
{
	Hash,
	Compare,
	Clone
}

/// <summary>
/// B8a/B8c: разбор <c>#[derive(...)]</c> на объявлениях <c>type</c>/<c>unit</c>. Аргументы парсер
/// хранит как сырой текст (<see cref="AttributeNode.Arguments"/>), поэтому каталог сравнивает
/// имена ordinal и знает только реализованные протоколы: <c>hash</c>, <c>compare</c> и
/// <c>clone</c> (<c>to_str</c>, <c>to_debug</c>, <c>json</c> — следующие слайсы B8).
/// Несколько атрибутов <c>#[derive(...)]</c> на одном объявлении объединяются.
/// </summary>
public static class DeriveCatalog
{
	public const string AttributeName = "derive";

	private static readonly (string Name, DerivedProtocol Protocol)[] Entries =
	[
		("hash", DerivedProtocol.Hash),
		("compare", DerivedProtocol.Compare),
		("clone", DerivedProtocol.Clone)
	];

	private static readonly IReadOnlyList<AttributeNode> None = [];

	/// <summary>Реализованные имена протоколов в порядке каталога (для диагностик).</summary>
	public static IReadOnlyList<string> KnownNames { get; } = [.. Entries.Select(entry => entry.Name)];

	/// <summary>Перечень протоколов для текста диагностики: <c>hash, compare</c>.</summary>
	public static string KnownText => string.Join(", ", KnownNames);

	/// <summary>Разбирает имя аргумента <c>#[derive(...)]</c>; регистр важен.</summary>
	public static bool TryParse(string name, out DerivedProtocol protocol)
	{
		foreach (var (entryName, entryProtocol) in Entries)
		{
			if (!string.Equals(entryName, name, StringComparison.Ordinal))
				continue;
			protocol = entryProtocol;
			return true;
		}

		protocol = default;
		return false;
	}

	/// <summary>Все атрибуты <c>#[derive]</c> объявления в порядке исходника.</summary>
	public static IReadOnlyList<AttributeNode> Attributes(SyntaxNode? declaration) =>
		Attributes(DeclaredAttributesOf(declaration));

	/// <summary>Все атрибуты <c>#[derive]</c> из списка атрибутов в порядке исходника.</summary>
	public static IReadOnlyList<AttributeNode> Attributes(IReadOnlyList<AttributeNode>? attributes)
	{
		if (attributes is null)
			return None;

		List<AttributeNode>? result = null;
		foreach (var attribute in attributes)
		{
			if (!string.Equals(attribute.Name, AttributeName, StringComparison.Ordinal))
				continue;
			(result ??= []).Add(attribute);
		}

		return result ?? None;
	}

	/// <summary>Первый атрибут <c>#[derive]</c>, объявляющий протокол (для span'а диагностики).</summary>
	public static AttributeNode? Find(SyntaxNode? declaration, DerivedProtocol protocol) =>
		Find(DeclaredAttributesOf(declaration), protocol);

	/// <summary>Первый атрибут <c>#[derive]</c> из списка, объявляющий протокол.</summary>
	public static AttributeNode? Find(
		IReadOnlyList<AttributeNode>? attributes,
		DerivedProtocol protocol)
	{
		foreach (var attribute in Attributes(attributes))
		{
			foreach (var argument in attribute.Arguments)
			{
				if (TryParse(argument, out var parsed) && parsed == protocol)
					return attribute;
			}
		}

		return null;
	}

	/// <summary>Объявлен ли протокол у объявления (все <c>#[derive]</c> объединяются).</summary>
	public static bool Has(SyntaxNode? declaration, DerivedProtocol protocol) =>
		Find(declaration, protocol) is not null;

	private static IReadOnlyList<AttributeNode>? DeclaredAttributesOf(SyntaxNode? declaration) => declaration switch
	{
		TypeDeclaration type => type.DeclaredAttributes,
		UnitDeclaration unit => unit.DeclaredAttributes,
		_ => null
	};
}
