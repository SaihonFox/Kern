using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Vm.Execution;
using Kern.Vm.Reflection;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Reflection;

/// <summary>
/// Построитель типа во время выполнения (`reflect.define_type("Point3")`): поля и атрибуты, затем `build()` возвращает значение `type`.
/// Созданный тип — обычный тип модуля: `t.create([...])`, `t.fields`, `is`, `f.get(obj)` работают как у объявленных в коде.
/// </summary>
[KernType("type_builder")]
public sealed class KernTypeBuilder(string name, Interpreter vm)
{
	private readonly List<(string Name, KType Type, List<(string, KValue[])> Attrs)> _fields = [];
	private readonly List<(string Name, KValue[] Args)> _attrs = [];
	private bool _unit, _immutable;

	/// <summary>Делает тип unit (значение, структурное сравнение); `immutable` — как `unit!`.</summary>
	[KernMember] public KernTypeBuilder AsUnit(bool immutable = false) { _unit = true; _immutable = immutable; return this; }

	/// <summary>Добавляет поле.</summary>
	[KernMember] public KernTypeBuilder Field(string fieldName, KType type) { _fields.Add((fieldName, type, [])); return this; }

	/// <summary>Атрибут последнего добавленного поля: `.field("id", i32).field_attribute("column", ["pk"])`.</summary>
	[KernMember] public KernTypeBuilder FieldAttribute(string attrName, List<KValue>? args = null)
	{
		if (_fields.Count == 0) throw new KernPanic("field_attribute: сначала добавьте поле");
		_fields[^1].Attrs.Add((attrName, [.. args ?? []]));
		return this;
	}

	/// <summary>Атрибут типа: `.attribute("entity", ["users"])`.</summary>
	[KernMember] public KernTypeBuilder Attribute(string attrName, List<KValue>? args = null) { _attrs.Add((attrName, [.. args ?? []])); return this; }

	/// <summary>Создаёт тип в модуле. Имя должно быть новым.</summary>
	[KernMember] public KType Build() =>
		DynamicTypes.Define(vm, name, _unit, _immutable,
			[.. _fields.Select(f => new DynamicTypes.FieldSpec(f.Name, f.Type, f.Attrs))], _attrs);
}
