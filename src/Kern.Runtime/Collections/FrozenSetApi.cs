using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Неизменяемое множество <c>frozen_set&lt;T&gt;</c> (spec §16): копия элементов обычного
/// <c>set&lt;T&gt;</c> в порядке источника. Изменения источника на копию не влияют.
/// </summary>
[KernType("frozen_set", GenericArity = 1)]
public sealed class KernFrozenSet : IKernCollection
{
	private readonly KValue[] _items;

	/// <summary>Скопировать элементы множества.</summary>
	public KernFrozenSet(KValue source)
	{
		_items = source.Ref switch
		{
			KNativeBox { Value: IKernCollection collection } => [.. collection.Items],
			_ => throw new KernPanic($"frozen_set: ожидался set, получено {source.TypeName}"),
		};
	}

	/// <summary>Число элементов.</summary>
	public int Count => _items.Length;

	/// <summary>Элементы в порядке источника.</summary>
	public IEnumerable<KValue> Items => _items;

	/// <summary>Есть ли элемент.</summary>
	public bool Contains(KValue item) => _items.Contains(item);

	/// <summary>Элемент по индексу в порядке источника.</summary>
	public KValue Get(int index)
	{
		int actual = index < 0 ? _items.Length + index : index;
		if ((uint)actual >= (uint)_items.Length)
			throw new KernPanic($"frozen_set: индекс {index} вне множества (длина {_items.Length})");
		return _items[actual];
	}

	/// <summary>Копия элементов как обычный <c>list&lt;T&gt;</c>.</summary>
	public KList ToList() => new(null, _items);

	public override string ToString() => $"frozen_set({_items.Length})";
}

/// <summary>
/// Фабрика и члены <c>frozen_set&lt;T&gt;</c> (spec §16): <c>frozen_set(set)</c>,
/// свойства <c>len</c>/<c>is_empty</c>, <c>get/contains/to_list</c>.
/// </summary>
[KernType("frozen_set", GenericArity = 1)]
public static class FrozenSetApi
{
	/// <summary><c>frozen_set(items)</c>: неизменяемая копия множества.</summary>
	[KernCtor]
	[KernName("frozen_set")]
	[KernReturn("frozen_set<T>")]
	public static KValue Create<T>([KernParameterType("set<T>")] KValue items) =>
		KValue.Obj(new KNativeBox(new KernFrozenSet(items)));

	/// <summary><c>len</c>: число элементов.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>is_empty</c>: множество пусто.</summary>
	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KNativeBox self) => Require(self).Count == 0;

	/// <summary><c>get(i) -&gt; T</c>: элемент по индексу в порядке источника.</summary>
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

	private static KernFrozenSet Require(KNativeBox self) => self.Value switch
	{
		KernFrozenSet set => set,
		_ => throw new KernPanic($"ожидался frozen_set, получено {self.Value.GetType().Name}"),
	};
}
