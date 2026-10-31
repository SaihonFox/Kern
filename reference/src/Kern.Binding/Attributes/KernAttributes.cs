namespace Kern.Binding.Attributes;

public enum KernTypeKind { Class, Unit, ImmutableUnit, Interface, Enum }

/// <summary>Привязывает C#-класс к Kern-типу. Статический класс с первым параметром `self` = члены чужого/встроенного типа.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Interface)]
public sealed class KernTypeAttribute(string name, string? module = null) : Attribute
{
	public string Name { get; } = name;
	/// <summary>Kern-модуль (use kern.text[str]); если не задан — берётся из [KernModule] на сборке/namespace-классе.</summary>
	public string? Module { get; } = module;
	public KernTypeKind Kind { get; init; } = KernTypeKind.Class;
	public bool Open { get; init; }
}

/// <summary>Сборка/класс принадлежит Kern-модулю (например "kern.text").</summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = true)]
public sealed class KernModuleAttribute(string name, string? @namespace = null) : Attribute
{
	public string Name { get; } = name;
	/// <summary>C#-namespace, который отображается в этот модуль.</summary>
	public string? Namespace { get; } = @namespace;
}

/// <summary>Метод. Перегрузки C# = перегрузки Kern (каждая — отдельный FunctionDef).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class KernMemberAttribute(string? name = null) : Attribute
{
	public string? Name { get; } = name;
}

/// <summary>Свойство. Применяется к C#-свойству ({get;set;}/{get;init;}) или к статическому методу-геттеру `Len(self)`.
/// Для метода-сеттера используется Setter = "SetLen".</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method)]
public sealed class KernPropertyAttribute(string? name = null) : Attribute
{
	public string? Name { get; } = name;
	public string? Setter { get; init; }
}

[AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method)]
public sealed class KernCtorAttribute : Attribute;

/// <summary>Статический член типа (#fun).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class KernStaticAttribute(string? name = null) : Attribute
{
	public string? Name { get; } = name;
}

/// <summary>Оператор: [KernOperator("+")] static T Add(T a, T b), либо экземплярный.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class KernOperatorAttribute(string symbol) : Attribute
{
	public string Symbol { get; } = symbol;
}

[AttributeUsage(AttributeTargets.All)]
public sealed class KernNameAttribute(string name) : Attribute
{
	public string Name { get; } = name;
}

[AttributeUsage(AttributeTargets.All)]
public sealed class KernIgnoreAttribute : Attribute;

/// <summary>Параметр KType[] получает reified generic-аргументы вызова (typeof<T>()).</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class KernTypeArgsAttribute : Attribute;

/// <summary>Параметр Interpreter — доступ к VM (вызов лямбд, планировщик).</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class KernVmAttribute : Attribute;

/// <summary>Метод/тип — пользовательский атрибут Kern (`type X[attribute]`) на стороне C#.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class KernAttributeTypeAttribute(string name) : Attribute
{
	public string Name { get; } = name;
}

/// <summary>Ограничения generic-параметров C# → Kern: where T : IKernNum, IKernHashable, ...</summary>
public interface IKernNum;
public interface IKernInum : IKernNum;
public interface IKernFnum : IKernNum;
public interface IKernHashable;
public interface IKernComparable;
