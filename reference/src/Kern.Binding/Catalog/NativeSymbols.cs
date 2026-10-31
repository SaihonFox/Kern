using System.Reflection;
using Kern.Bytecode;

namespace Kern.Binding.Catalog;

/// <summary>Вид нативного члена.</summary>
public enum NativeMemberKind { Method, Property, Ctor, Operator, Static, Field }

/// <summary>Как C#-класс представлен в Kern.</summary>
public enum NativeTypeCategory
{
	/// <summary>Статический класс с `self`: члены чужого/встроенного типа (str, list&lt;T&gt;, num…).</summary>
	Extension,
	/// <summary>Статический класс без `self`: набор статик-членов (`math`, `random`).</summary>
	StaticHolder,
	Class, Unit, ImmutableUnit, Interface, Enum,
}

/// <summary>Generic-параметр метода (Scope=1) или типа (Scope=0) вместе с Kern-ограничениями.</summary>
public sealed class NativeGenericParam
{
	public required string Name { get; init; }
	public required int Index { get; init; }
	public required int Scope { get; init; }
	public IReadOnlyList<TypeSig> Constraints { get; init; } = [];
}

/// <summary>Видимый Kern-параметр (скрытые [KernVm]/[KernTypeArgs] и `self` сюда не входят).</summary>
public sealed class NativeParam
{
	public required string Name { get; init; }
	public required TypeSig Type { get; init; }
	public bool HasDefault { get; init; }
	/// <summary>null / bool / long / ulong / double / string / char (enum → long).</summary>
	public object? DefaultValue { get; init; }
	public bool IsVariadic { get; init; }
	public required ParameterInfo Clr { get; init; }
}

public sealed class NativeMember
{
	/// <summary>Уникальный стабильный ключ (см. <see cref="NativeKey"/>). Для свойств — ключ геттера, иначе сеттера.</summary>
	public required string Key { get; init; }
	public required string Name { get; init; }
	public required NativeMemberKind Kind { get; init; }
	public required NativeTypeSymbol? Owner { get; init; }
	public required MemberInfo Clr { get; init; }

	/// <summary>Kern-статический (#fun). Для экстеншенов с self — false.</summary>
	public bool IsStatic { get; init; }
	/// <summary>Есть параметр `self` (или это экземплярный метод нативного класса).</summary>
	public bool IsInstance { get; init; }
	/// <summary>Тип `self` (для экстеншенов и экземплярных членов).</summary>
	public TypeSig? SelfType { get; init; }

	public IReadOnlyList<NativeGenericParam> GenericParams { get; init; } = [];
	public IReadOnlyList<NativeParam> Params { get; init; } = [];
	public TypeSig Return { get; init; } = TypeSig.Void;
	public bool IsAsync { get; init; }
	/// <summary>Для Operator: "+", "-", "==", "[]"…</summary>
	public string? OpSymbol { get; init; }

	// Свойства
	public string? GetterKey { get; init; }
	public string? SetterKey { get; init; }
	public bool CanGet => GetterKey is not null;
	public bool CanSet => SetterKey is not null && !IsInit;
	/// <summary>Сеттер только при инициализации ({ get; init; }).</summary>
	public bool IsInit { get; init; }
	public TypeSig? PropertyType { get; init; }

	public override string ToString() => $"{Owner?.FullName}.{Name}({string.Join(", ", Params.Select(p => p.Name))})";
}

public sealed class NativeTypeSymbol
{
	public required string Name { get; init; }
	public required string Module { get; init; }
	public string FullName => $"{Module}.{Name}";
	public required NativeTypeCategory Category { get; init; }
	/// <summary>Основной C#-тип (для расширений — первый класс с таким именем).</summary>
	public required Type ClrType { get; init; }
	/// <summary>Все C#-классы, внёсшие члены (partial/несколько файлов).</summary>
	public List<Type> ClrTypes { get; } = [];
	public IReadOnlyList<NativeGenericParam> GenericParams { get; init; } = [];
	/// <summary>Подпись самого типа (для расширений — расширяемого: str, list&lt;T&gt;…).</summary>
	public required TypeSig Sig { get; init; }
	public bool IsOpen { get; init; }
	/// <summary>Базовый нативный тип: для [KernType]-классов берётся из C#-наследования (база тоже [KernType]), для dotnet — из BaseType.</summary>
	public TypeSig? Parent { get; init; }
	/// <summary>Символ базового типа (заполняется каталогом после сканирования всех сборок).</summary>
	public NativeTypeSymbol? ParentSymbol { get; set; }
	public List<NativeMember> Members { get; } = [];
	public List<string> EnumNames { get; } = [];
	public List<long> EnumValues { get; } = [];
	public bool IsExtension => Category == NativeTypeCategory.Extension;

	public override string ToString() => FullName;
}

public sealed class NativeModule(string name)
{
	public string Name { get; } = name;
	public Dictionary<string, NativeTypeSymbol> Types { get; } = [];
	/// <summary>Свободные функции модуля (println, panic…): [KernMember] в классах с [KernModule].</summary>
	public List<NativeMember> Functions { get; } = [];
}
