using Kern.Binding.Attributes;
using Kern.Binding.Formatting;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Члены встроенных <c>list</c>/<c>array</c>/<c>dict</c>. Приёмник приходит как
/// <see cref="KValue"/>: для generic-контейнеров это позволяет не терять reified-тип
/// и поддерживать отрицательные индексы (DECISIONS §I).
/// </summary>
[KernType("list", IsBuiltin = true)]
public static class ListApi
{
	[KernProperty]
	public static int Len(KValue self) => CollectionOps.ToList(self).Count;

	[KernProperty]
	public static bool IsEmpty(KValue self) => CollectionOps.ToList(self).Count == 0;

	[KernName("add")]
	[KernMember]
	public static void Add(KValue self, KValue item) => CollectionOps.ToList(self).Add(item);

	[KernName("push")]
	[KernMember]
	public static void Push(KValue self, KValue item) => CollectionOps.ToList(self).Add(item);

	[KernMember]
	public static KValue Get(KValue self, int index) =>
		CollectionOps.ToList(self)[CollectionOps.Normalize(index, CollectionOps.ToList(self).Count)];

	[KernMember]
	public static void Set(KValue self, int index, KValue value)
	{
		var list = CollectionOps.ToList(self);
		list[CollectionOps.Normalize(index, list.Count)] = value;
	}

	[KernName("remove_at")]
	[KernMember]
	public static KValue RemoveAt(KValue self, int index)
	{
		var list = CollectionOps.ToList(self);
		int actual = CollectionOps.Normalize(index, list.Count);
		KValue removed = list[actual];
		list.Items.RemoveAt(actual);
		return removed;
	}

	[KernMember]
	public static bool Remove(KValue self, KValue item) => CollectionOps.ToList(self).Items.Remove(item);

	[KernName("contains")]
	[KernMember]
	public static bool Contains(KValue self, KValue item) => CollectionOps.ToList(self).Items.Contains(item);

	[KernName("index_of")]
	[KernMember]
	public static int IndexOf(KValue self, KValue item) => CollectionOps.ToList(self).Items.IndexOf(item);

	[KernMember]
	public static void Clear(KValue self) => CollectionOps.ToList(self).Items.Clear();

	[KernName("first")]
	[KernMember]
	public static KValue First(KValue self)
	{
		var list = CollectionOps.ToList(self);
		if (list.Count == 0)
			throw new KernPanic("first: пустой список");
		return list[0];
	}

	[KernName("last")]
	[KernMember]
	public static KValue Last(KValue self)
	{
		var list = CollectionOps.ToList(self);
		if (list.Count == 0)
			throw new KernPanic("last: пустой список");
		return list[list.Count - 1];
	}

	[KernName("slice")]
	[KernMember]
	public static KValue Slice(KValue self, int start, int end)
	{
		var list = CollectionOps.ToList(self);
		int from = CollectionOps.Normalize(start, list.Count);
		int to = CollectionOps.Normalize(end, list.Count);
		if (from < 0 || to > list.Count || from > to)
			throw new KernPanic($"slice: недопустимый диапазон {start}..{end} для списка из {list.Count}");
		return CollectionOps.ListValue(list.Items.Skip(from).Take(to - from));
	}

	[KernMember]
	public static KValue Reverse(KValue self)
	{
		var list = CollectionOps.ToList(self);
		return CollectionOps.ListValue(list.Items.AsEnumerable().Reverse());
	}

	[KernName("map")]
	[KernMember]
	public static KValue Map(Interpreter interpreter, KValue self, KValue function)
	{
		var list = CollectionOps.ToList(self);
		var mapped = new List<KValue>(list.Count);
		foreach (KValue item in list.Items)
			mapped.Add(interpreter.RunValue(function, item));
		return KValue.Obj(new KList(list.ElementType, mapped));
	}

	[KernMember]
	public static KValue Filter(Interpreter interpreter, KValue self, KValue function)
	{
		var list = CollectionOps.ToList(self);
		var filtered = new List<KValue>();
		foreach (KValue item in list.Items)
		{
			KValue keep = interpreter.RunValue(function, item);
			if (keep.Kind != ValueKind.Bool)
				throw new KernPanic($"filter: предикат вернул {keep.TypeName}, ожидался bool");
			if (keep.AsBool)
				filtered.Add(item);
		}

		return KValue.Obj(new KList(list.ElementType, filtered));
	}

	[KernName("join")]
	[KernMember]
	public static string Join(KValue self, string separator) =>
		string.Join(separator, CollectionOps.ToList(self).Items.Select(Kern.Binding.Formatting.KernFormat.Value));
}

/// <summary>Члены встроенного <c>array&lt;T&gt;</c>.</summary>
[KernType("array", IsBuiltin = true)]
public static class ArrayApi
{
	[KernProperty]
	public static int Len(KValue self) => CollectionOps.ToArray(self).Length;

	[KernProperty]
	public static bool IsEmpty(KValue self) => CollectionOps.ToArray(self).Length == 0;

	[KernMember]
	public static KValue Get(KValue self, int index)
	{
		var array = CollectionOps.ToArray(self);
		return array[CollectionOps.Normalize(index, array.Length)];
	}

	[KernMember]
	public static void Set(KValue self, int index, KValue value)
	{
		var array = CollectionOps.ToArray(self);
		array[CollectionOps.Normalize(index, array.Length)] = value;
	}

	[KernName("contains")]
	[KernMember]
	public static bool Contains(KValue self, KValue item) =>
		CollectionOps.ToArray(self).Items.Contains(item);

	[KernName("slice")]
	[KernMember]
	public static KValue Slice(KValue self, int start, int end)
	{
		var array = CollectionOps.ToArray(self);
		int from = CollectionOps.Normalize(start, array.Length);
		int to = CollectionOps.Normalize(end, array.Length);
		if (from < 0 || to > array.Length || from > to)
			throw new KernPanic($"slice: недопустимый диапазон {start}..{end} для массива из {array.Length}");
		return CollectionOps.ListValue(array.Items[from..to].ToArray());
	}
}

/// <summary>Члены встроенного <c>dict&lt;K,V&gt;</c>.</summary>
[KernType("dict", IsBuiltin = true)]
public static class DictApi
{
	[KernProperty]
	public static int Len(KValue self) => CollectionOps.ToDict(self).Count;

	[KernProperty]
	public static bool IsEmpty(KValue self) => CollectionOps.ToDict(self).Count == 0;

	[KernName("contains_key")]
	[KernMember]
	public static bool ContainsKey(KValue self, KValue key) => CollectionOps.ToDict(self).TryGet(key, out _);

	[KernMember]
	public static KValue Get(KValue self, KValue key) => CollectionOps.ToDict(self).Get(key);

	/// <summary>Значение по ключу или запасное значение; отсутствие ключа не пишет в словарь.</summary>
	[KernName("get_or")]
	[KernMember]
	public static KValue GetOr(KValue self, KValue key, KValue fallback) =>
		CollectionOps.ToDict(self).TryGet(key, out KValue value) ? value : fallback;

	[KernMember]
	public static void Set(KValue self, KValue key, KValue value) => CollectionOps.ToDict(self).Set(key, value);

	[KernMember]
	public static bool Remove(KValue self, KValue key) => CollectionOps.ToDict(self).Remove(key);

	[KernMember]
	public static void Clear(KValue self)
	{
		var dict = CollectionOps.ToDict(self);
		foreach (var item in dict.Items.ToList())
			dict.Remove(item.Key);
	}

	[KernName("keys")]
	[KernMember]
	public static KValue Keys(KValue self) =>
		CollectionOps.ListValue(CollectionOps.ToDict(self).Items.Select(pair => pair.Key));

	[KernName("values")]
	[KernMember]
	public static KValue Values(KValue self) =>
		CollectionOps.ListValue(CollectionOps.ToDict(self).Items.Select(pair => pair.Value));

	/// <summary>`dict.keys` без скобок — свойство (spec §12/пример 09).</summary>
	[KernName("keys")]
	[KernProperty]
	public static KValue KeysProperty(KValue self) => Keys(self);

	/// <summary>`dict.values` без скобок — свойство.</summary>
	[KernName("values")]
	[KernProperty]
	public static KValue ValuesProperty(KValue self) => Values(self);
}

/// <summary>Приведение KValue к коллекциям и общие проверки индексов.</summary>
internal static class CollectionOps
{
	public static KList ToList(KValue value) => value.Ref as KList
		?? throw new KernPanic($"ожидался list, получено {value.TypeName}");

	public static KArray ToArray(KValue value) => value.Ref as KArray
		?? throw new KernPanic($"ожидался array, получено {value.TypeName}");

	public static KDict ToDict(KValue value) => value.Ref as KDict
		?? throw new KernPanic($"ожидался dict, получено {value.TypeName}");

	public static KValue ListValue(IEnumerable<KValue> items) =>
		KValue.Obj(new KList(null, items));

	public static int Normalize(int index, int count) => index < 0 ? count + index : index;
}
