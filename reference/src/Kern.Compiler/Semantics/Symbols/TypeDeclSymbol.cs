using Kern.Compiler.Semantics.Scopes;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Symbols;

public enum TypeDeclKind
{
	Class,          // type
	AbstractClass,  // @type
	Unit,           // unit
	ImmutableUnit,  // unit!
	Enum,
	Union,          // type X = [A, B]
	Attribute,
}

/// <summary>Родитель в списке наследования: тип + аргументы `[A(x * 2)]` (не вычислены — это часть 2).</summary>
public sealed record ParentInfo(TypeSymbol Type, IReadOnlyList<Argument> Args, ParentRef Syntax)
{
	public TypeDeclSymbol? Decl => (Type as DeclaredType)?.Decl;
}

/// <summary>Generic-параметр типа или функции с ограничениями.</summary>
public sealed class TypeParamSymbol(string name, int index, Symbol owner) : Symbol(name)
{
	public override SymbolKind Kind => SymbolKind.TypeParam;
	public int Index { get; } = index;
	public Symbol Owner { get; } = owner;
	/// <summary>0 — параметр типа, 1 — параметр функции (как в <c>TypeSig.Param</c>).</summary>
	public int Scope => Owner is FunctionSymbol ? 1 : 0;
	/// <summary>Ограничения-типы `T[IHashable]` (заполняются после разрешения заголовков).</summary>
	public List<TypeSymbol> Bounds { get; } = [];
	/// <summary>Ограничения конструктора `V[ctor(i32, str)]`.</summary>
	public List<IReadOnlyList<TypeSymbol>> CtorConstraints { get; } = [];
	public TypeParamType AsType => field ??= new TypeParamType(this);
	public override string FullName => Name;
}

/// <summary>Объявление типа: type/@type/unit/unit!/enum/union-алиас/attribute.</summary>
public sealed class TypeDeclSymbol(string name, TypeDeclKind kind) : Symbol(name)
{
	public override SymbolKind Kind => SymbolKind.Type;
	/// <summary>Вид объявления (type/@type/unit/...); <see cref="Kind"/> — всегда SymbolKind.Type.</summary>
	public TypeDeclKind DeclKind { get; } = kind;

	public Decl? Syntax { get; set; }
	public bool IsOpen { get; set; }
	public bool IsAbstract => DeclKind == TypeDeclKind.AbstractClass;
	public bool IsValueType => DeclKind is TypeDeclKind.Unit or TypeDeclKind.ImmutableUnit;

	public List<TypeParamSymbol> TypeParams { get; } = [];
	public List<ParentInfo> Parents { get; } = [];

	public List<FieldSymbol> Fields { get; } = [];
	public List<PropertySymbol> Properties { get; } = [];
	/// <summary>Методы и операторы (без аксессоров свойств и конструкторов).</summary>
	public List<FunctionSymbol> Functions { get; } = [];
	public List<FunctionSymbol> Constructors { get; } = [];
	public List<TypeDeclSymbol> NestedTypes { get; } = [];
	/// <summary>Элементы enum (также присутствуют в <see cref="Fields"/> с IsEnumEntry).</summary>
	public List<FieldSymbol> EnumEntries { get; } = [];
	/// <summary>Члены union-алиаса.</summary>
	public List<TypeSymbol> UnionMembers { get; } = [];

	public FunctionSymbol? PrimaryConstructor { get; set; }
	/// <summary>Область видимости членов (имя → символ/группа перегрузок), родитель — область файла.</summary>
	public Scope? MemberScope { get; set; }

	/// <summary>Тип с собственными параметрами в качестве аргументов (внутри объявления).</summary>
	public DeclaredType ThisType => field ??= DeclaredType.Create(this, [.. TypeParams.Select(p => (TypeSymbol)p.AsType)]);

	public DeclaredType Instantiate(IReadOnlyList<TypeSymbol> args) => DeclaredType.Create(this, args);
}
