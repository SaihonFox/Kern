using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections.Containers;

/// <summary>Множество уникальных значений. Порядок обхода — порядок добавления.</summary>
[KernType("set")]
public sealed class KernSet<T> : KernCollection<T>
{
	private readonly List<KValue> _order = [];
	private readonly HashSet<KValue> _items = new(KValueComparer.Instance);

	[KernCtor] public KernSet() { }

	[KernProperty] public override int Len => _items.Count;
	[KernProperty] public override bool IsEmpty => _items.Count == 0;

	/// <summary>false — значение уже было в множестве.</summary>
	[KernMember] public bool Add(T item)
	{
		var v = Kv.Of(item);
		if (!_items.Add(v)) return false;
		_order.Add(v);
		return true;
	}

	[KernMember] public bool Remove(T item)
	{
		var v = Kv.Of(item);
		if (!_items.Remove(v)) return false;
		int i = _order.FindIndex(x => KValueComparer.Instance.Equals(x, v));
		if (i >= 0) _order.RemoveAt(i);
		return true;
	}

	[KernMember] public override bool Contains(T item) => _items.Contains(Kv.Of(item));

	[KernOperator("in")] public override bool In(T item) => _items.Contains(Kv.Of(item));

	[KernMember] public override void Clear()
	{
		_items.Clear();
		_order.Clear();
	}

	[KernMember] public override KList<T> ToList() => Kv.List<T>(_order);

	/// <summary>Для `loop var x in set`.</summary>
	[KernMember] public override KList<T> Iter() => Kv.List<T>(_order);

	/// <summary>Объединение: новое множество.</summary>
	[KernMember] public KernSet<T> Union(KernSet<T> other)
	{
		var r = new KernSet<T>();
		foreach (var x in _order) r.AddValue(x);
		foreach (var x in other._order) r.AddValue(x);
		return r;
	}

	[KernMember] public KernSet<T> Intersect(KernSet<T> other)
	{
		var r = new KernSet<T>();
		foreach (var x in _order) if (other._items.Contains(x)) r.AddValue(x);
		return r;
	}

	[KernMember] public KernSet<T> Difference(KernSet<T> other)
	{
		var r = new KernSet<T>();
		foreach (var x in _order) if (!other._items.Contains(x)) r.AddValue(x);
		return r;
	}

	[KernMember] public bool IsSubsetOf(KernSet<T> other) => _order.All(other._items.Contains);

	private void AddValue(KValue v)
	{
		if (_items.Add(v)) _order.Add(v);
	}
}
