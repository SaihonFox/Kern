using Kern.Bytecode;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Reflection;

public sealed class ParameterInfo(string name, KType type, ParamDef def, int position)
{
	public string Name { get; } = name;
	public KType Type { get; } = type;
	public int Position { get; } = position;
	public bool HasDefault => def.HasDefault;
	public bool IsVariadic => def.IsVariadic;
	public override string ToString() => $"{Name}: {Type.Name}";
}
