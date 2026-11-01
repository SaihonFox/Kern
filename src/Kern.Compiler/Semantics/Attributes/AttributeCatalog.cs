using Kern.Bytecode;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Attributes;

/// <summary>
/// Каталог встроенных атрибутов (B7, spec §7). Проверяются только имена: аргументы, кратность
/// и допустимые цели пока не валидируются (исключение — <c>#[derive]</c>, его разбирает
/// <see cref="DeriveCatalog"/>, B8a). <c>#[cfg]</c> и <c>#[test]</c> зарезервированы:
/// их поведение появится вместе с M14/stage 2, сейчас это принятые no-op.
/// </summary>
public static class AttributeCatalog
{
	private static readonly string[] Names =
	[
		"deprecated",
		"must_use",
		"inline",
		"no_inline",
		"cfg",
		"test",
		"derive",
		// HTTP-прокси (spec round 10): размечают абстрактные методы http_client.proxy.
		"get",
		"post",
		"put",
		"patch",
		"delete"
	];

	private static readonly HashSet<string> Known = new(Names, StringComparer.Ordinal);

	/// <summary>Имена известных атрибутов в порядке, пригодном для текста диагностики.</summary>
	public static IReadOnlyList<string> KnownNames { get; } = Array.AsReadOnly(Names);

	/// <summary>Известен ли атрибут каталогу (сравнение ordinal, регистр важен).</summary>
	public static bool IsKnown(string name) => Known.Contains(name);

	/// <summary>Есть ли среди атрибутов объявления атрибут с данным именем.</summary>
	public static bool Has(IReadOnlyList<AttributeNode>? attributes, string name) =>
		Find(attributes, name) is not null;

	/// <summary>
	/// Есть ли атрибут у объявления. Учитываются <see cref="TypeDeclaration"/>,
	/// <see cref="UnitDeclaration"/> и <see cref="FunctionDeclaration"/>; для null и прочих
	/// узлов — false.
	/// </summary>
	public static bool Has(SyntaxNode? declaration, string name) => Find(declaration, name) is not null;

	/// <summary>Первый атрибут с данным именем (для текста диагностики) или null.</summary>
	public static AttributeNode? Find(IReadOnlyList<AttributeNode>? attributes, string name)
	{
		if (attributes is null)
			return null;
		foreach (var attribute in attributes)
		{
			if (string.Equals(attribute.Name, name, StringComparison.Ordinal))
				return attribute;
		}
		return null;
	}

	/// <summary>Первый атрибут с данным именем у объявления или null.</summary>
	public static AttributeNode? Find(SyntaxNode? declaration, string name) => declaration switch
	{
		TypeDeclaration type => Find(type.DeclaredAttributes, name),
		UnitDeclaration unit => Find(unit.DeclaredAttributes, name),
		FunctionDeclaration function => Find(function.DeclaredAttributes, name),
		_ => null
	};

	/// <summary>Есть ли атрибут в байткодовом списке символа (spec §7).</summary>
	public static bool Has(IReadOnlyList<AttributeUse>? attributes, string name) =>
		Find(attributes, name) is not null;

	/// <summary>Первый атрибут символа с данным именем или null.</summary>
	public static AttributeUse? Find(IReadOnlyList<AttributeUse>? attributes, string name)
	{
		if (attributes is null)
			return null;
		foreach (var attribute in attributes)
		{
			if (string.Equals(attribute.Name, name, StringComparison.Ordinal))
				return attribute;
		}
		return null;
	}
}
