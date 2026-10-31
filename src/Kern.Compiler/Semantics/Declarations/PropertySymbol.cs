using Kern.Bytecode;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Declarations;

/// <summary>
/// Свойство <c>get</c>/<c>set</c>/<c>init</c> (spec §4). Accessor'ы — отдельные функции,
/// автосвойство получает скрытое backing-поле.
/// </summary>
public sealed record PropertySymbol(
	string Name,
	KernType Type,
	FunctionSymbol? Getter,
	FunctionSymbol? Setter,
	FunctionSymbol? Init,
	bool IsAuto,
	FieldSymbol? BackingField,
	Accessibility Accessibility,
	bool IsStatic,
	bool IsOverride,
	bool IsAbstract,
	ExpressionNode? ExpressionBody,
	ExpressionNode? Initializer)
{
	public TypeSymbol? Owner { get; init; }

	public SyntaxNode? Declaration { get; init; }

	/// <summary>Свойство-расширение (<c>i32 str.len</c>); хранится в реестре расширений.</summary>
	public bool IsExtension { get; init; }

	public KernType? ReceiverType { get; init; }

	/// <summary>
	/// Атрибуты свойства; копируются в accessor-функции, потому что <c>PropertyDef</c>
	/// компилятор пока не выпускает (см. KNOWN_ISSUES).
	/// </summary>
	public IReadOnlyList<AttributeUse> Attributes { get; init; } = [];

	public bool CanRead => Getter is not null;

	public bool CanWrite => Setter is not null;

	public bool CanInit => Init is not null;
}
