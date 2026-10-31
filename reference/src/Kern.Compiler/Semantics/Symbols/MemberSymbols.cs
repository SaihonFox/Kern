using Kern.Binding.Catalog;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Symbols;

/// <summary>Поле типа, глобальная переменная, элемент enum или backing-поле автосвойства.</summary>
public sealed class FieldSymbol(string name, TypeSymbol? type) : Symbol(name)
{
	public override SymbolKind Kind => SymbolKind.Field;
	/// <summary>null — `var x = ...`, тип выводится из инициализатора (часть 2).</summary>
	public TypeSymbol? Type { get; set; } = type;
	public bool IsStatic { get; set; }
	/// <summary>`!str name` — обязательное поле.</summary>
	public bool IsRequired { get; set; }
	public Expr? Init { get; set; }
	public FieldDecl? Syntax { get; set; }

	/// <summary>Скрытое поле автосвойства.</summary>
	public PropertySymbol? BackingFor { get; set; }
	/// <summary>Поле порождено параметром первичного конструктора.</summary>
	public ParameterSymbol? FromParameter { get; set; }
	public bool IsEnumEntry { get; set; }
	public EnumEntry? EnumSyntax { get; set; }
	/// <summary>Значение/аргументы элемента enum (не вычислены).</summary>
	public IReadOnlyList<Argument> EnumArgs { get; set; } = [];
	/// <summary>Выбор родителя `str $[A].name`: имя и разрешённый тип.</summary>
	public string? ParentSelector { get; set; }
	public TypeSymbol? ParentSelectorType { get; set; }
	public bool IsGlobal => Container is ModuleSymbol;
}

/// <summary>Свойство. Геттер/сеттер/init — отдельные <see cref="FunctionSymbol"/>.</summary>
public sealed class PropertySymbol(string name, TypeSymbol type) : Symbol(name)
{
	public override SymbolKind Kind => SymbolKind.Property;
	public TypeSymbol Type { get; set; } = type;
	public FunctionSymbol? Getter { get; set; }
	public FunctionSymbol? Setter { get; set; }
	public FunctionSymbol? Initer { get; set; }
	public bool IsAuto { get; set; }
	public bool IsStatic { get; set; }
	public FieldSymbol? BackingField { get; set; }
	/// <summary>Начальное значение `{ get; set } = 5`.</summary>
	public Expr? Init { get; set; }
	public PropertyDecl? Syntax { get; set; }
	/// <summary>Расширение свойства `i32 str.len2 body` (тогда есть только геттер).</summary>
	public TypeSymbol? ExtensionOf { get; set; }
	public string? ParentSelector { get; set; }
	public TypeSymbol? ParentSelectorType { get; set; }
	/// <summary>Для нативных свойств.</summary>
	public NativeMember? Native { get; set; }

	public bool CanRead => Getter is not null || Native is { CanGet: true };
	public bool CanWrite => Setter is not null || Native is { CanSet: true };
	public bool CanInit => Initer is not null || Native is { IsInit: true };
}

/// <summary>Локальная переменная (создаётся в части 2).</summary>
public sealed class LocalSymbol(string name, TypeSymbol? type) : Symbol(name)
{
	public override SymbolKind Kind => SymbolKind.Local;
	public TypeSymbol? Type { get; set; } = type;
	/// <summary>`!` — одноразовое присваивание.</summary>
	public bool IsSingleAssign { get; set; }
	public override string FullName => Name;
}
