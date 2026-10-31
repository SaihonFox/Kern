using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Reflection;

public sealed class FieldInfo
{
	private readonly Interpreter _vm;
	private readonly FieldDef _def;
	private readonly TypeDef _owner;

	internal FieldInfo(Interpreter vm, TypeInfo declaring, TypeDef owner, FieldDef def)
	{
		_vm = vm; _def = def; _owner = owner; Declaring = declaring;
	}

	public TypeInfo Declaring { get; }
	public string Name => _def.Name;
	public bool IsStatic => _def.Flags.HasFlag(MemberFlags.Static);
	public MemberAccess Access => Reflector.AccessOf(_def.Flags, MemberAccess.Private);
	public KType Type => _vm.Types.Resolve(_def.Type, (Declaring.Type as KClassType)?.Args ?? [], []);
	public bool HasAttribute(string name) => _def.Attributes.Any(a => Reflector.NameMatches(a.TypeName, name));
	public IReadOnlyList<AttributeInfo> Attributes => AttributeInfo.Wrap(_vm, _def.Attributes);

	public KValue GetValue(KValue target)
	{
		if (IsStatic)
		{
			int i = Reflector.StaticIndex(_vm, _owner, _def);
			return i >= 0 ? _vm.Info.Statics[i] : throw new KernPanic($"статическое поле {Name} не найдено");
		}
		return target.Ref is KObject o ? o.Fields[_def.Slot] : throw new KernPanic($"поле {Name}: нужен объект");
	}

	public void SetValue(KValue target, KValue value)
	{
		value = Reflector.Coerce(value, Type);
		if (IsStatic)
		{
			int i = Reflector.StaticIndex(_vm, _owner, _def);
			if (i < 0) throw new KernPanic($"статическое поле {Name} не найдено");
			_vm.Info.Statics[i] = value;
			return;
		}
		if (target.Ref is KObject o) o.Fields[_def.Slot] = value;
		else throw new KernPanic($"поле {Name}: нужен объект");
	}

	public override string ToString() => $"{Name}: {Type.Name}";
}
