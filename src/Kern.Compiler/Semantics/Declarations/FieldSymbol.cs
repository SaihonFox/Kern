using Kern.Bytecode;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Declarations;

/// <summary>
/// Поле типа или глобальное поле. <c>!</c> у поля — обязательность инициализации;
/// у глобального поля — константа (spec §4).
/// </summary>
public sealed record FieldSymbol(
	string Name,
	KernType Type,
	bool IsRequired,
	bool IsStatic,
	Accessibility Accessibility,
	ExpressionNode? Initializer,
	bool IsConst)
{
	/// <summary>Тип, объявивший поле; <c>null</c> у глобального поля.</summary>
	public TypeSymbol? Owner { get; init; }

	/// <summary>Поле создано автоматически для параметра конструктора с <c>$</c>.</summary>
	public bool IsAutoInit { get; init; }

	/// <summary>Скрытое backing-поле автосвойства; не участвует в проверках имён.</summary>
	public bool IsBackingField { get; init; }

	/// <summary>Выбор поля родителя: <c>str $[A]name</c>.</summary>
	public string? ParentSelector { get; init; }

	public string? ParentSelectorType { get; init; }

	public SyntaxNode? Declaration { get; init; }

	/// <summary>Атрибуты поля в байткодовой форме; codegen пишет их в <c>FieldDef</c>.</summary>
	public IReadOnlyList<AttributeUse> Attributes { get; init; } = [];
}