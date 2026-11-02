using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Неизменяемый список <c>frozen_list&lt;T&gt;</c> (spec §16): копия элементов обычного
/// <c>list&lt;T&gt;</c>. Изменения источника на копию не влияют.
/// </summary>
[KernType("frozen_list", GenericArity = 1)]
public sealed class KernFrozenList : IKernCollection
{
	private readonly KValue[] _items;

	/// <summary>Скопировать элементы списка.</summary>
	public KernFrozenList(KValue source)
	{
		_items = [.. CollectionOps.ToList(source).Items];
	}

	/// <summary>Число элементов.</summary>
	public int Count => _items.Length;

	/// <summary>Элементы в исходном порядке.</summary>
	public IEnumerable<KValue> Items => _items;

	/// <summary>Есть ли элемент.</summary>
	public bool Contains(KValue item) => _items.Contains(item);

	/// <summary>Элемент по индексу; отрицательный индекс считается с конца.</summary>
	public KValue Get(int index)
	{
		int actual = index < 0 ? _items.Length + index : index;
		if ((uint)actual >= (uint)_items.Length)
			throw new KernPanic($"frozen_list: индекс {index} вне списка (длина {_items.Length})");
		return _items[actual];
	}

	/// <summary>Копия элементов как обычный <c>list&lt;T&gt;</c>.</summary>
	public KList ToList() => new(null, _items);

	public override string ToString() => $"frozen_list({_items.Length})";
}

/// <summary>
/// Фабрика и члены <c>frozen_list&lt;T&gt;</c> (spec §16): <c>frozen_list(list)</c>,
/// свойства <c>len</c>/<c>is_empty</c>, <c>get/contains/to_list</c>.
/// </summary>
[KernType("frozen_list", GenericArity = 1)]
public static class FrozenListApi
{
	/// <summary><c>frozen_list(items)</c>: неизменяемая копия списка.</summary>
	[KernCtor]
	[KernName("frozen_list")]
	[KernReturn("frozen_list<T>")]
	public static KValue Create<T>([KernParameterType("list<T>")] KValue items) =>
		KValue.Obj(new KNativeBox(new KernFrozenList(items)));

	/// <summary><c>len</c>: число элементов.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>is_empty</c>: список пуст.</summary>
	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KNativeBox self) => Require(self).Count == 0;

	/// <summary><c>get(i) -&gt; T</c>: элемент по индексу.</summary>
	[KernMember]
	[KernReturn("T")]
	public static KValue Get(KNativeBox self, int index) => Require(self).Get(index);

	/// <summary><c>contains(v)</c>: есть ли элемент.</summary>
	[KernMember]
	public static bool Contains(KNativeBox self, KValue item) => Require(self).Contains(item);

	/// <summary><c>to_list() -&gt; list&lt;T&gt;</c>: копия элементов.</summary>
	[KernName("to_list")]
	[KernMember]
	[KernReturn("list<T>")]
	public static KValue ToList(KNativeBox self) => KValue.Obj(Require(self).ToList());

	private static KernFrozenList Require(KNativeBox self) => self.Value switch
	{
		KernFrozenList list => list,
		_ => throw new KernPanic($"ожидался frozen_list, получено {self.Value.GetType().Name}"),
	};
}
