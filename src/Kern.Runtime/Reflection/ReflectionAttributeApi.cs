using Kern.Binding.Attributes;
using Kern.Bytecode;
using Kern.Vm.Values;

namespace Kern.Runtime.Reflection;

/// <summary>Дополнительные члены <c>attribute_info</c> (spec round 18).</summary>
public static partial class AttributeInfoApi
{
	/// <summary><c>matches(name)</c>: совпадает ли имя атрибута.</summary>
	[KernMember]
	public static bool Matches(KNativeBox self, string name) => Require(self).Attribute.Name == name;

	/// <summary><c>named(key)</c>: значение именованного аргумента <c>key = value</c>.</summary>
	[KernName("named")]
	[KernMember]
	[KernReturn("str?")]
	public static KValue Named(KNativeBox self, string key)
	{
		IReadOnlyList<KConst> args = Require(self).Attribute.Args;
		for (var index = 0; index + 1 < args.Count; index++)
		{
			if (args[index].Kind != KConstKind.Str || args[index].AsStr != key)
				continue;
			int valueIndex = index + 1;
			if (valueIndex < args.Count && args[valueIndex].Kind == KConstKind.Str && args[valueIndex].AsStr == "=")
				valueIndex++;
			if (valueIndex < args.Count)
				return ToValue(args[valueIndex]);
		}

		return KValue.Nil;
	}
}
