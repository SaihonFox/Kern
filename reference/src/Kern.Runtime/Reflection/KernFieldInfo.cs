using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Vm.Reflection;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Reflection;

/// <summary>Поле типа: чтение/запись значения у объекта (у статического поля target не нужен).</summary>
[KernType("field_info")]
public sealed class KernFieldInfo(FieldInfo info)
{
	[KernProperty] public string Name => info.Name;
	[KernProperty] public KType Type => info.Type;
	[KernProperty] public bool IsStatic => info.IsStatic;
	[KernProperty] public string Access => info.Access.ToString().ToLowerInvariant();
	[KernProperty] public KList<KernAttributeInfo> Attributes => Wrapping.Wrap<KernAttributeInfo>(info.Attributes.Select(a => new KernAttributeInfo(a)));

	[KernMember] public bool HasAttribute(string name) => info.HasAttribute(name);
	[KernMember] public KernAttributeInfo? Attribute(string name) => info.Attributes.FirstOrDefault(a => a.Is(name)) is { } a ? new KernAttributeInfo(a) : null;

	/// <summary>Значение поля у объекта <paramref name="target"/> (Kotlin: `prop.get(obj)`).</summary>
	[KernMember] public KValue Get(KValue target) => info.GetValue(target);

	[KernMember] public void Set(KValue target, KValue value) => info.SetValue(target, value);

	[KernMember] public override string ToString() => info.ToString();
}
