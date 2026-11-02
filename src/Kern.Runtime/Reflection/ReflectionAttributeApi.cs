using Kern.Binding.Attributes;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Reflection;

/// <summary>Дополнительные члены <c>attribute_info</c> (spec round 18).</summary>
public static partial class AttributeInfoApi
{
	/// <summary><c>matches(name)</c>: совпадает ли имя атрибута.</summary>
	[KernMember]
	public static bool Matches(KNativeBox self, string name) => Require(self).Attribute.Name == name;

	/// <summary>
	/// <c>instantiate()</c>: создать объект типа атрибута, заполнив поля позиционными
	/// или именованными аргументами (<c>key = value</c>). Тип ищется в пуле модуля по имени.
	/// </summary>
	[KernMember]
	[KernReturn("object")]
	public static KValue Instantiate(Interpreter interpreter, KNativeBox self)
	{
		AttributeUse attribute = Require(self).Attribute;
		TypeDef? definition = interpreter.Module.TypePool.FirstOrDefault(type =>
			string.Equals(type.Name, attribute.Name, StringComparison.OrdinalIgnoreCase) ||
			type.FullName.EndsWith("." + attribute.Name, StringComparison.OrdinalIgnoreCase));
		if (definition is null)
			throw new KernPanic($"attribute_info.instantiate: тип атрибута '{attribute.Name}' не найден");
		KValue[] fields = new KValue[definition.Fields.Count];
		Array.Fill(fields, KValue.None);
		int position = 0;
		for (var index = 0; index < attribute.Args.Count; index++)
		{
			if (index + 2 < attribute.Args.Count &&
				attribute.Args[index].Kind == KConstKind.Str &&
				attribute.Args[index + 1].Kind == KConstKind.Str &&
				attribute.Args[index + 1].AsStr == "=")
			{
				string name = attribute.Args[index].AsStr;
				FieldDef? field = definition.Fields.FirstOrDefault(item => item.Name == name);
				if (field is not null && field.Slot >= 0 && field.Slot < fields.Length)
				{
					fields[field.Slot] = ToValue(attribute.Args[index + 2]);
					index += 2;
					continue;
				}
			}

			if (position < fields.Length)
				fields[position++] = ToValue(attribute.Args[index]);
		}

		return KValue.Obj(new KObject(interpreter.Types.Named(definition), fields));
	}

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
