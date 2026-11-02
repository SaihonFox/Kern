using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Упорядоченное множество <c>sorted_set&lt;T&gt;</c> (spec §16): уникальные значения,
/// всегда в возрастающем порядке общего сравнения. Внутри — отсортированный список.
/// </summary>
[KernType("sorted_set", GenericArity = 1)]
public sealed class KernSortedSet : IKernCollection
{
	private readonly List<KValue> _items = [];

	/// <summary>Число элементов.</summary>
	public int Count => _items.Count;

	/// <summary>Элементы по возрастанию.</summary>
	public IEnumerable<KValue> Items => _items;

	/// <summary>Есть ли элемент (обычное равенство <see cref="KValue"/>).</summary>
	public bool Contains(KValue item) => _items.Contains(item);

	/// <summary>Добавить значение; повтор игнорируется.</summary>
	public void Add(KValue item, Interpreter interpreter)
	{
		int index = LowerBound(item, interpreter);
		if (index < _items.Count &&
			KernValueOrdering.Compare(_items[index], item, interpreter) == 0)
		{
			return;
		}

		_items.Insert(index, item);
	}

	/// <summary>Удалить значение; <c>true</c>, если оно было.</summary>
	public bool Remove(KValue item, Interpreter interpreter)
	{
		int index = LowerBound(item, interpreter);
		if (index >= _items.Count ||
			KernValueOrdering.Compare(_items[index], item, interpreter) != 0)
		{
			return false;
		}

		_items.RemoveAt(index);
		return true;
	}

	/// <summary>Очистить множество.</summary>
	public void Clear() => _items.Clear();

	/// <summary>Копия элементов по возрастанию как <c>list&lt;T&gt;</c>.</summary>
	public KList ToList() => new(null, _items);

	/// <summary>Первый индекс, где элемент не меньше <paramref name="item"/>.</summary>
	private int LowerBound(KValue item, Interpreter interpreter)
	{
		int low = 0;
		int high = _items.Count;
		while (low < high)
		{
			int middle = low + (high - low) / 2;
			if (KernValueOrdering.Compare(_items[middle], item, interpreter) < 0)
				low = middle + 1;
			else
				high = middle;
		}

		return low;
	}

	public override string ToString() => $"sorted_set({_items.Count})";
}

/// <summary>
/// Члены и фабрика <c>sorted_set&lt;T&gt;</c> (spec §16): <c>add</c>, <c>remove</c>,
/// <c>contains</c>, <c>clear</c>, свойства <c>len</c>/<c>is_empty</c>, <c>to_list</c>.
/// Элементы упорядочены по общему порядку значений.
/// </summary>
[KernType("sorted_set", GenericArity = 1)]
public static class SortedSetApi
{
	/// <summary><c>sorted_set&lt;T&gt;()</c>: пустое упорядоченное множество.</summary>
	[KernCtor]
	[KernName("sorted_set")]
	[KernReturn("sorted_set<T>")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernSortedSet()));

	/// <summary><c>len</c>: число элементов.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>is_empty</c>: множество пусто.</summary>
	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KNativeBox self) => Require(self).Count == 0;

	/// <summary><c>add(v)</c>: добавить, повтор игнорируется.</summary>
	[KernMember]
	public static void Add(Interpreter interpreter, KNativeBox self, KValue item) =>
		Require(self).Add(item, interpreter);

	/// <summary><c>remove(v)</c>: удалить; <c>true</c>, если элемент был.</summary>
	[KernMember]
	public static bool Remove(Interpreter interpreter, KNativeBox self, KValue item) =>
		Require(self).Remove(item, interpreter);

	/// <summary><c>contains(v)</c>: есть ли элемент (его же использует <c>in</c>).</summary>
	[KernMember]
	public static bool Contains(KNativeBox self, KValue item) => Require(self).Contains(item);

	/// <summary><c>clear()</c>: очистить множество.</summary>
	[KernMember]
	public static void Clear(KNativeBox self) => Require(self).Clear();

	/// <summary><c>to_list() -&gt; list&lt;T&gt;</c>: элементы по возрастанию.</summary>
	[KernName("to_list")]
	[KernMember]
	[KernReturn("list<T>")]
	public static KValue ToList(KNativeBox self) => KValue.Obj(Require(self).ToList());

	private static KernSortedSet Require(KNativeBox self) => self.Value switch
	{
		KernSortedSet set => set,
		_ => throw new KernPanic($"ожидался sorted_set, получено {self.Value.GetType().Name}"),
	};
}
