using Kern.Binding.Attributes;
using Kern.Binding.Marshalling;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Reflection;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Reflection;

/// <summary>
/// Члены значения `type` (`typeof&lt;T&gt;()` / `typeof(value)`): имя, состав, атрибуты, создание экземпляров.
/// Сочетает Kotlin (`type.properties`, `prop.get(obj)`) и C# (`GetMethod`, `CreateInstance`).
/// </summary>
[KernType("type")]
public static class TypeApi
{
	private static TypeInfo Info(Interpreter vm, KType t) => Reflector.TypeOf(vm, t);

	[KernProperty] public static string Name(KType self, [KernVm] Interpreter vm) => Info(vm, self).Name;
	[KernProperty] public static string FullName(KType self, [KernVm] Interpreter vm) => Info(vm, self).FullName;
	[KernProperty] public static bool IsDeclared(KType self, [KernVm] Interpreter vm) => Info(vm, self).IsDeclared;
	[KernProperty] public static bool IsEnum(KType self, [KernVm] Interpreter vm) => Info(vm, self).IsEnum;
	[KernProperty] public static bool IsUnit(KType self, [KernVm] Interpreter vm) => Info(vm, self).IsUnit;
	[KernProperty] public static bool IsAbstract(KType self, [KernVm] Interpreter vm) => Info(vm, self).IsAbstract;
	[KernProperty] public static bool IsInterface(KType self, [KernVm] Interpreter vm) => Info(vm, self).IsInterface;

	/// <summary>Generic-аргументы (`list<i32>` → [i32]).</summary>
	[KernProperty] public static KList<KType> Args(KType self, [KernVm] Interpreter vm) =>
		Kv.List<KType>(Info(vm, self).GenericArguments.Select(Marshaller.FromKType));

	[KernProperty] public static KList<KType> Parents(KType self, [KernVm] Interpreter vm) =>
		Kv.List<KType>(Info(vm, self).Parents.Select(p => Marshaller.FromKType(p.Type)));

	[KernProperty] public static KList<KernFieldInfo> Fields(KType self, [KernVm] Interpreter vm) =>
		Wrapping.Wrap<KernFieldInfo>(Info(vm, self).Fields.Select(f => new KernFieldInfo(f)));

	[KernProperty] public static KList<KernPropertyInfo> Properties(KType self, [KernVm] Interpreter vm) =>
		Wrapping.Wrap<KernPropertyInfo>(Info(vm, self).Properties.Select(p => new KernPropertyInfo(p)));

	[KernProperty] public static KList<KernMethodInfo> Methods(KType self, [KernVm] Interpreter vm) =>
		Wrapping.Wrap<KernMethodInfo>(Info(vm, self).Methods.Select(m => new KernMethodInfo(m)));

	[KernProperty] public static KList<KernMethodInfo> Constructors(KType self, [KernVm] Interpreter vm) =>
		Wrapping.Wrap<KernMethodInfo>(Info(vm, self).Constructors.Select(m => new KernMethodInfo(m)));

	[KernProperty] public static KList<KernAttributeInfo> Attributes(KType self, [KernVm] Interpreter vm) =>
		Wrapping.Wrap<KernAttributeInfo>(Info(vm, self).Attributes.Select(a => new KernAttributeInfo(a)));

	/// <summary>Имена членов enum (union без данных).</summary>
	[KernMember] public static KList<string> EnumNames(KType self, [KernVm] Interpreter vm) =>
		Kv.Strs(Info(vm, self).EnumMembers.Select(m => m.Name));

	[KernMember] public static bool HasAttribute(KType self, string name, [KernVm] Interpreter vm) => Info(vm, self).HasAttribute(name);

	[KernMember] public static KernAttributeInfo? Attribute(KType self, string name, [KernVm] Interpreter vm) =>
		Info(vm, self).GetAttribute(name) is { } a ? new KernAttributeInfo(a) : null;

	[KernMember] public static KernFieldInfo? GetField(KType self, string name, [KernVm] Interpreter vm) =>
		Info(vm, self).GetField(name) is { } f ? new KernFieldInfo(f) : null;

	[KernMember] public static KernPropertyInfo? GetProperty(KType self, string name, [KernVm] Interpreter vm) =>
		Info(vm, self).GetProperty(name) is { } p ? new KernPropertyInfo(p) : null;

	/// <summary>Все перегрузки метода с таким именем.</summary>
	[KernMember] public static KList<KernMethodInfo> GetMethods(KType self, string name, [KernVm] Interpreter vm) =>
		Wrapping.Wrap<KernMethodInfo>(Info(vm, self).GetMethods(name).Select(m => new KernMethodInfo(m)));

	/// <summary>Первый метод с таким именем (nil, если нет).</summary>
	[KernMember] public static KernMethodInfo? GetMethod(KType self, string name, [KernVm] Interpreter vm) =>
		Info(vm, self).GetMethods(name).FirstOrDefault() is { } m ? new KernMethodInfo(m) : null;

	/// <summary>Читает свойство или поле по имени (`t.get(obj, "name")`).</summary>
	[KernMember] public static KValue Get(KType self, KValue target, string name, [KernVm] Interpreter vm) => Info(vm, self).GetMember(target, name);

	[KernMember] public static void Set(KType self, KValue target, string name, KValue value, [KernVm] Interpreter vm)
	{
		if (target.Ref is not KObject o) throw new KernPanic("set: нужен объект Kern-типа");
		Info(vm, self).SetMember(o, name, value);
	}

	/// <summary>Создаёт экземпляр: конструктор подбирается по числу и типам аргументов (C#: Activator.CreateInstance).</summary>
	[KernMember] public static KValue Create(KType self, List<KValue>? args, [KernVm] Interpreter vm) =>
		Info(vm, self).CreateInstance([.. args ?? []]);

	[KernMember] public static bool IsInstance(KType self, KValue value, [KernVm] Interpreter vm) => Info(vm, self).IsInstance(value);

	/// <summary>`a.is_assignable_from(b)`: значение типа b можно использовать как a.</summary>
	[KernMember] public static bool IsAssignableFrom(KType self, KType other, [KernVm] Interpreter vm) =>
		Info(vm, self).IsAssignableFrom(Info(vm, other));
}

/// <summary>Поиск типов и функций модуля: `reflect.find_type("User")`, `reflect.types_with_attribute("entity")`.</summary>
[KernType("reflect")]
public static class ReflectApi
{
	[KernStatic] public static KList<KType> Types([KernVm] Interpreter vm) =>
		Kv.List<KType>(Reflector.DeclaredTypes(vm).Select(t => Marshaller.FromKType(t.Type)));

	[KernStatic] public static KType? FindType(string name, [KernVm] Interpreter vm) => Reflector.FindType(vm, name)?.Type;

	[KernStatic] public static KList<KType> TypesWithAttribute(string attribute, [KernVm] Interpreter vm) =>
		Kv.List<KType>(Reflector.TypesWithAttribute(vm, attribute).Select(t => Marshaller.FromKType(t.Type)));

	[KernStatic] public static KList<KernMethodInfo> Functions([KernVm] Interpreter vm) =>
		Wrapping.Wrap<KernMethodInfo>(Reflector.Functions(vm).Select(m => new KernMethodInfo(m)));

	[KernStatic] public static KernMethodInfo? FindFunction(string name, [KernVm] Interpreter vm) =>
		Reflector.FindFunction(vm, name) is { } m ? new KernMethodInfo(m) : null;

	[KernStatic] public static KList<KernMethodInfo> FunctionsWithAttribute(string attribute, [KernVm] Interpreter vm) =>
		Wrapping.Wrap<KernMethodInfo>(Reflector.FunctionsWithAttribute(vm, attribute).Select(m => new KernMethodInfo(m)));

	/// <summary>Начать описание нового типа во время выполнения: `reflect.define_type("Point3").field("x", f64).build()`.</summary>
	[KernStatic] public static KernTypeBuilder DefineType(string name, [KernVm] Interpreter vm) => new(name, vm);

	/// <summary>Атрибуты модуля (`#![name]`).</summary>
	[KernStatic] public static KList<KernAttributeInfo> ModuleAttributes([KernVm] Interpreter vm) =>
		Wrapping.Wrap<KernAttributeInfo>(Reflector.ModuleAttributes(vm).Select(a => new KernAttributeInfo(a)));
}
