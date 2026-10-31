using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections.Containers;

/// <summary>
/// Наблюдаемый список. `on_change(fun(str kind, T item) { … })` вызывается после каждого изменения:
/// kind = "add" | "remove" | "set" | "clear" (для "clear" item — значение по умолчанию/none).
/// Имена членов совпадают с list/set (len, is_empty, add, remove, contains, clear, iter, to_list).
/// </summary>
[KernType("observable")]
public sealed class KernObservable<T> : KernCollection<T>
{
	private readonly List<KValue> _items = [];
	private readonly List<KAction<string, T>> _watchers = [];

	[KernCtor] public KernObservable() { }

	[KernProperty] public override int Len => _items.Count;
	[KernProperty] public override bool IsEmpty => _items.Count == 0;

	[KernMember] public void OnChange(KAction<string, T> watcher) => _watchers.Add(watcher);

	private void Notify(string kind, KValue item)
	{
		var k = KValue.Str(kind);
		foreach (var w in _watchers.ToArray()) w.Invoke(k, item);
	}

	[KernMember] public void Add(T item)
	{
		var v = Kv.Of(item);
		_items.Add(v);
		Notify("add", v);
	}

	[KernMember] public void Insert(int index, T item)
	{
		var v = Kv.Of(item);
		_items.Insert(index, v);
		Notify("add", v);
	}

	[KernMember] public bool Remove(T item)
	{
		var v = Kv.Of(item);
		int i = _items.FindIndex(x => KValueComparer.Instance.Equals(x, v));
		if (i < 0) return false;
		_items.RemoveAt(i);
		Notify("remove", v);
		return true;
	}

	[KernMember] public T At(int index) => Kv.As<T>(_items[index]);

	[KernMember] public void Set(int index, T item)
	{
		var v = Kv.Of(item);
		_items[index] = v;
		Notify("set", v);
	}

	[KernMember] public override bool Contains(T item) => _items.Contains(Kv.Of(item), KValueComparer.Instance);

	[KernOperator("in")] public override bool In(T item) => Contains(item);

	[KernMember] public override void Clear()
	{
		_items.Clear();
		Notify("clear", KValue.None);
	}

	[KernMember] public override KList<T> ToList() => Kv.List<T>(_items);

	/// <summary>Для `loop var x in obs`.</summary>
	[KernMember] public override KList<T> Iter() => Kv.List<T>(_items);
}
