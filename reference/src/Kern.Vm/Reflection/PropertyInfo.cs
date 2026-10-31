using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Reflection;

public sealed class PropertyInfo
{
	private readonly Interpreter _vm;
	private readonly PropertyDef _def;

	internal PropertyInfo(Interpreter vm, TypeInfo declaring, PropertyDef def)
	{
		_vm = vm; _def = def; Declaring = declaring;
	}

	public TypeInfo Declaring { get; }
	public string Name => _def.Name;
	public bool IsStatic => _def.Flags.HasFlag(MemberFlags.Static);
	public MemberAccess Access => Reflector.AccessOf(_def.Flags, MemberAccess.Private);
	public KType Type => _vm.Types.Resolve(_def.Type, (Declaring.Type as KClassType)?.Args ?? [], []);
	public bool CanRead => _def.Getter >= 0;
	public bool CanWrite => _def.Setter >= 0;
	public bool IsInitOnly => _def.Setter < 0 && _def.Init >= 0;
	public bool HasAttribute(string name) => _def.Attributes.Any(a => Reflector.NameMatches(a.TypeName, name));
	public IReadOnlyList<AttributeInfo> Attributes => AttributeInfo.Wrap(_vm, _def.Attributes);

	public KValue GetValue(KValue target)
	{
		if (_def.Getter < 0) throw new KernPanic($"у свойства {Name} нет getter");
		return Reflector.MethodOf(_vm, _def.Getter, Declaring).Invoke(target);
	}

	public void SetValue(KValue target, KValue value)
	{
		int fn = _def.Setter >= 0 ? _def.Setter : _def.Init;
		if (fn < 0) throw new KernPanic($"свойство {Name} только для чтения");
		Reflector.MethodOf(_vm, fn, Declaring).Invoke(target, value);
	}

	public override string ToString() => $"{Name}: {Type.Name}";
}
