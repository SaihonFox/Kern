using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Reflection;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Reflection;

/// <summary>Применённый атрибут `#[Name(args)]`: имя, позиционные и именованные аргументы; можно создать экземпляр.</summary>
[KernType("attribute_info")]
public sealed class KernAttributeInfo(AttributeInfo info)
{
	[KernProperty] public string Name => info.TypeName;

	/// <summary>Позиционные аргументы.</summary>
	[KernProperty] public KList<object> Args => Kv.List<object>(info.Positional);

	/// <summary>Именованные аргументы `#[Route(path: "/x")]`.</summary>
	[KernProperty] public KDict<string, object> Named
	{
		get
		{
			var d = new KDictObject(TypeFactory.Str, TypeFactory.Object);
			foreach (var (k, v) in info.Named) d.Items[KValue.Str(k)] = v;
			return new KDict<string, object>(d);
		}
	}

	[KernMember] public bool Matches(string name) => info.Is(name);

	/// <summary>Создаёт объект атрибута (если тип атрибута объявлен в модуле): позиционные → конструктор, именованные → свойства.</summary>
	[KernMember] public KValue Instantiate() => info.Instantiate();

	[KernMember] public override string ToString() => info.ToString();
}
