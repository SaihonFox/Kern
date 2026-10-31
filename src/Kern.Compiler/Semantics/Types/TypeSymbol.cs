using System.Runtime.CompilerServices;
using Kern.Bytecode;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Types;

/// <summary>Вид объявленного символа типа (не путать с <c>Semantics.SymbolKind</c> таблицы модуля).</summary>
public enum SymbolKind
{
	Class,
	AbstractClass,
	Unit,
	ImmutableUnit,
	UnionAlias,
	Native
}

/// <summary>
/// Символ объявленного типа. Slice D наполнит его членами; сейчас хранятся идентичность и
/// прямые родители (<see cref="Bases"/>), которые лениво заполняет <see cref="TypeResolver"/>.
/// </summary>
public sealed record TypeSymbol
{
	private readonly StructuralList<string> _genericParameterNames = StructuralList<string>.Empty;
	private IReadOnlyList<KernType> _bases = [];

	public required string Name { get; init; }

	public required SymbolKind Kind { get; init; }

	public IReadOnlyList<string> GenericParameterNames
	{
		get => _genericParameterNames;
		init => _genericParameterNames = StructuralList<string>.From(value);
	}

	public DeclarationNode? Declaration { get; init; }

	public string? ModuleName { get; internal set; }

	public Accessibility Accessibility { get; internal set; }

	public bool IsAbstract { get; internal set; }

	public bool IsOpen { get; internal set; }

	public bool IsStatic { get; internal set; }

	/// <summary>
	/// Атрибуты объявления (spec §7) в байткодовой форме: сборщик объявлений заполняет
	/// список из <see cref="AttributeNode"/>, codegen переносит его в <c>TypeDef</c>.
	/// </summary>
	public IReadOnlyList<AttributeUse> Attributes { get; internal set; } = [];

	/// <summary>Unit-типы имеют value-семантику; `unit!` дополнительно неизменяем (spec §9).</summary>
	public bool IsValueType => Kind is SymbolKind.Unit or SymbolKind.ImmutableUnit;

	/// <summary>`unit!` — неизменяемый unit; у обычного `unit` false.</summary>
	public bool IsImmutable => Kind == SymbolKind.ImmutableUnit;

	/// <summary>
	/// Параметры обобщения с уже разрешёнными ограничениями. Заполняет сборщик объявлений
	/// (slice D); до этого — только имена в <see cref="GenericParameterNames"/>.
	/// </summary>
	public IReadOnlyList<TypeParameterType> GenericParameters { get; internal set; } = [];

	/// <summary>Члены типа: методы, поля, свойства, конструкторы, операторы, выбор родителей.</summary>
	public MemberTable Members { get; internal set; } = MemberTable.Empty;

	/// <summary>Альтернативы union-алиаса, разрешённые в типы; у остальных типов пусто.</summary>
	public IReadOnlyList<KernType> UnionAlternatives { get; internal set; } = [];

	/// <summary>Синтезированный или явный primary-конструктор (<c>type X(...)</c>), если есть.</summary>
	public FunctionSymbol? PrimaryConstructor { get; internal set; }

	/// <summary>
	/// Прямые родители типа в том виде, как они записаны в объявлении: generic-параметры
	/// самого типа представлены <see cref="TypeParameterType"/>. У <c>unit</c> родителей нет.
	/// </summary>
	public IReadOnlyList<KernType> Bases
	{
		get => _bases;
		init => _bases = value ?? [];
	}

	public int GenericArity => _genericParameterNames.Count;

	/// <summary>Заполнение родителей во время разрешения типа; список ещё не опубликован наружу.</summary>
	internal void SetBases(IReadOnlyList<KernType> bases) =>
		_bases = bases ?? throw new ArgumentNullException(nameof(bases));

	/// <summary>
	/// Равенство символов намеренно не включает <see cref="Bases"/>: родители могут ссылаться
	/// обратно на символ (цикл наследования), и структурное сравнение ушло бы в бесконечную рекурсию.
	/// </summary>
	public bool Equals(TypeSymbol? other) =>
		other is not null &&
		Name == other.Name &&
		Kind == other.Kind &&
		_genericParameterNames.Equals(other._genericParameterNames) &&
		ReferenceEquals(Declaration, other.Declaration) &&
		ModuleName == other.ModuleName;

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(Name, StringComparer.Ordinal);
		hash.Add(Kind);
		hash.Add(_genericParameterNames);
		hash.Add(Declaration is null ? 0 : RuntimeHelpers.GetHashCode(Declaration));
		hash.Add(ModuleName, StringComparer.Ordinal);
		return hash.ToHashCode();
	}
}
