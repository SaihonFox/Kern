using Kern.Binding.Attributes;
using Kern.Binding.Marshalling;
using Kern.Vm.Reflection;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Reflection;

/// <summary>Параметр функции: имя, тип, позиция.</summary>
[KernType("param_info")]
public sealed class KernParamInfo(ParameterInfo info)
{
	[KernProperty] public string Name => info.Name;
	[KernProperty] public KType Type => info.Type;
	[KernProperty] public int Position => info.Position;
	[KernProperty] public bool HasDefault => info.HasDefault;
	[KernProperty] public bool IsVariadic => info.IsVariadic;

	[KernMember] public override string ToString() => info.ToString();
}
