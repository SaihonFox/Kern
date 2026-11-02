using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Словарь с упорядоченными ключами <c>sorted_dict&lt;K, V&gt;</c> (spec §16):
/// пары хранятся по возрастанию общего порядка ключей, поэтому <c>keys</c>/<c>values</c>
/// и обход детерминированы. Внутри — отсортированный список пар.
/// </summary>
[KernType("sorted_dict", GenericArity = 2)]
public sealed class KernSortedDict : IKernCollection
{
	private readonly List<Entry> _items = [];

	/// <summary>Число пар.</summary>
	public int Count => _items.Count;

	/// <summary>Ключи по возрастанию (для общего протокола коллекций).</summary>
	public IEnumerable<KValue> Items => _items.Select(entry => entry.Key);

	/// <summary>Есть ли ключ (обычное равенство <see cref="KValue"/>).</summary>
	public bool Contains(KValue item) => _items.Any(entry => entry.Key.Equals(item));

	/// <summary>Записать значение; существующий ключ заменяет значение.</summary>
	public void Set(KValue key, KValue value, Interpreter interpreter)
	{
		int index = LowerBound(key, interpreter);
		if (index < _items.Count &&
			KernValueOrdering.Compare(_items[index].Key, key, interpreter) == 0)
		{
			_items[index].Value = value;
			return;
		}

		_items.Insert(index, new Entry(key, value));
	}

	/// <summary>Значение по ключу; отсутствие ключа — паника.</summary>
	public KValue Get(KValue key, Interpreter interpreter)
	{
		int index = IndexOf(key, interpreter);
		if (index < 0)
			throw new KernPanic($"sorted_dict: ключ {key} отсутствует");
		return _items[index].Value;
	}

	/// <summary>Значение по ключу или запасное значение.</summary>
	public KValue GetOr(KValue key, KValue fallback, Interpreter interpreter)
	{
		int index = IndexOf(key, interpreter);
		return index < 0 ? fallback : _items[index].Value;
	}

	/// <summary>Есть ли ключ.</summary>
	public bool ContainsKey(KValue key, Interpreter interpreter) => IndexOf(key, interpreter) >= 0;

	/// <summary>Удалить ключ; <c>true</c>, если он был.</summary>
	public bool Remove(KValue key, Interpreter interpreter)
	{
		int index = IndexOf(key, interpreter);
		if (index < 0)
			return false;
		_items.RemoveAt(index);
		return true;
	}

	/// <summary>Очистить словарь.</summary>
	public void Clear() => _items.Clear();

	/// <summary>Ключи по возрастанию.</summary>
	public KList Keys() => new(null, _items.Select(entry => entry.Key));

	/// <summary>Значения в порядке ключей.</summary>
	public KList Values() => new(null, _items.Select(entry => entry.Value));

	/// <summary>Общий протокол коллекции: <c>to_list</c> словаря — его ключи.</summary>
	public KList ToList() => Keys();

	/// <summary>Индекс ключа или -1.</summary>
	private int IndexOf(KValue key, Interpreter interpreter)
	{
		int index = LowerBound(key, interpreter);
		return index < _items.Count &&
			KernValueOrdering.Compare(_items[index].Key, key, interpreter) == 0
				? index
				: -1;
	}

	/// <summary>Первый индекс, где ключ не меньше <paramref name="key"/>.</summary>
	private int LowerBound(KValue key, Interpreter interpreter)
	{
		int low = 0;
		int high = _items.Count;
		while (low < high)
		{
			int middle = low + (high - low) / 2;
			if (KernValueOrdering.Compare(_items[middle].Key, key, interpreter) < 0)
				low = middle + 1;
			else
				high = middle;
		}

		return low;
	}

	public override string ToString() => $"sorted_dict({_items.Count})";

	private sealed class Entry(KValue key, KValue value)
	{
		public KValue Key { get; } = key;

		public KValue Value { get; set; } = value;
	}
}

/// <summary>
/// Члены и фабрика <c>sorted_dict&lt;K, V&gt;</c> (spec §16): <c>set/get/get_or</c>,
/// <c>contains_key/remove/clear</c>, свойства <c>len</c>/<c>is_empty</c>,
/// <c>keys/values/to_list</c>. Ключи упорядочены.
/// </summary>
[KernType("sorted_dict", GenericArity = 2)]
public static class SortedDictApi
{
	/// <summary><c>sorted_dict&lt;K, V&gt;()</c>: пустой словарь с упорядоченными ключами.</summary>
	[KernCtor]
	[KernName("sorted_dict")]
	[KernReturn("sorted_dict<T1,T2>")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernSortedDict()));

	/// <summary><c>len</c>: число пар.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>is_empty</c>: словарь пуст.</summary>
	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KNativeBox self) => Require(self).Count == 0;

	/// <summary><c>set(k, v)</c>: записать значение.</summary>
	[KernMember]
	public static void Set(Interpreter interpreter, KNativeBox self, KValue key, KValue value) =>
		Require(self).Set(key, value, interpreter);

	/// <summary><c>get(k) -&gt; V</c>: значение по ключу.</summary>
	[KernMember]
	[KernReturn("T2")]
	public static KValue Get(Interpreter interpreter, KNativeBox self, KValue key) =>
		Require(self).Get(key, interpreter);

	/// <summary><c>get_or(k, fallback) -&gt; V</c>: значение или запасное.</summary>
	[KernName("get_or")]
	[KernMember]
	[KernReturn("T2")]
	public static KValue GetOr(Interpreter interpreter, KNativeBox self, KValue key, KValue fallback) =>
		Require(self).GetOr(key, fallback, interpreter);

	/// <summary><c>contains_key(k)</c>: есть ли ключ.</summary>
	[KernName("contains_key")]
	[KernMember]
	public static bool ContainsKey(Interpreter interpreter, KNativeBox self, KValue key) =>
		Require(self).ContainsKey(key, interpreter);

	/// <summary><c>remove(k)</c>: удалить ключ; <c>true</c>, если он был.</summary>
	[KernMember]
	public static bool Remove(Interpreter interpreter, KNativeBox self, KValue key) =>
		Require(self).Remove(key, interpreter);

	/// <summary><c>clear()</c>: очистить словарь.</summary>
	[KernMember]
	public static void Clear(KNativeBox self) => Require(self).Clear();

	/// <summary><c>keys() -&gt; list&lt;K&gt;</c>: ключи по возрастанию.</summary>
	[KernName("keys")]
	[KernMember]
	[KernReturn("list<T1>")]
	public static KValue Keys(KNativeBox self) => KValue.Obj(Require(self).Keys());

	/// <summary><c>keys</c> без скобок — свойство (как у <c>dict</c>).</summary>
	[KernName("keys")]
	[KernProperty]
	public static KValue KeysProperty(KNativeBox self) => Keys(self);

	/// <summary><c>values() -&gt; list&lt;V&gt;</c>: значения в порядке ключей.</summary>
	[KernName("values")]
	[KernMember]
	[KernReturn("list<T2>")]
	public static KValue Values(KNativeBox self) => KValue.Obj(Require(self).Values());

	/// <summary><c>values</c> без скобок — свойство.</summary>
	[KernName("values")]
	[KernProperty]
	public static KValue ValuesProperty(KNativeBox self) => Values(self);

	/// <summary><c>to_list() -&gt; list&lt;K&gt;</c>: ключи (протокол коллекции).</summary>
	[KernName("to_list")]
	[KernMember]
	[KernReturn("list<T1>")]
	public static KValue ToList(KNativeBox self) => KValue.Obj(Require(self).ToList());

	/// <summary><c>contains(k)</c>: есть ли ключ (для <c>in</c>).</summary>
	[KernMember]
	public static bool Contains(Interpreter interpreter, KNativeBox self, KValue key) =>
		Require(self).ContainsKey(key, interpreter);

	private static KernSortedDict Require(KNativeBox self) => self.Value switch
	{
		KernSortedDict dict => dict,
		_ => throw new KernPanic($"ожидался sorted_dict, получено {self.Value.GetType().Name}"),
	};
}
