using Kern.Binding.Attributes;
using Kern.Binding.Marshalling;
using Kern.Binding.Wrappers;
using Kern.Vm.Reflection;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Reflection;

/// <summary>Функция, метод или конструктор модуля. Перегрузки — отдельные объекты.</summary>
[KernType("method_info")]
public sealed class KernMethodInfo(MethodInfo info)
{
	[KernProperty] public string Name => info.Name;
	[KernProperty] public KType ReturnType => info.ReturnType;
	[KernProperty] public bool IsStatic => info.IsStatic;
	[KernProperty] public bool IsAsync => info.IsAsync;
	[KernProperty] public bool IsConstructor => info.IsConstructor;
	[KernProperty] public bool IsOperator => info.IsOperator;
	[KernProperty] public bool IsAbstract => info.IsAbstract;
	[KernProperty] public string Access => info.Access.ToString().ToLowerInvariant();
	[KernProperty] public KList<KernParamInfo> Params => Wrapping.Wrap<KernParamInfo>(info.Parameters.Select(p => new KernParamInfo(p)));
	[KernProperty] public KList<KernAttributeInfo> Attributes => Wrapping.Wrap<KernAttributeInfo>(info.Attributes.Select(a => new KernAttributeInfo(a)));

	/// <summary>Тип-владелец (nil у верхнеуровневых функций).</summary>
	[KernProperty] public KType? Declaring => info.Declaring?.Type;

	[KernMember] public bool HasAttribute(string name) => info.HasAttribute(name);
	[KernMember] public KernAttributeInfo? Attribute(string name) => info.Attributes.FirstOrDefault(a => a.Is(name)) is { } a ? new KernAttributeInfo(a) : null;

	/// <summary>Вызвать у объекта <paramref name="target"/> (Kotlin: `fn.call(obj, args)`); аргументы приводятся к типам параметров.</summary>
	[KernMember] public KValue Call(KValue target, List<KValue>? args = null) => info.Invoke(target, [.. args ?? []]);

	/// <summary>Вызвать статическую/верхнеуровневую функцию.</summary>
	[KernMember] public KValue CallStatic(List<KValue>? args = null) => info.Invoke(default, [.. args ?? []]);

	[KernMember] public override string ToString() => info.ToString();
}
