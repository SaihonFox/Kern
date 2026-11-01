using Kern.Vm.Types;

namespace Kern.Vm.Values;

/// <summary>Словарь <c>dict&lt;K,V&gt;</c>; ключи сравниваются через <see cref="KValue"/>.</summary>
public sealed class KDict
{
	private readonly Dictionary<KValue, KValue> _items;

	public KDict(KType? keyType = null, KType? valueType = null, IEqualityComparer<KValue>? comparer = null)
	{
		KeyType = keyType;
		ValueType = valueType;
		_items = comparer is null ? [] : new Dictionary<KValue, KValue>(comparer);
	}

	public KType? KeyType { get; }

	public KType? ValueType { get; }

	public int Count => _items.Count;

	public bool TryGet(KValue key, out KValue value) => _items.TryGetValue(key, out value);

	public KValue Get(KValue key)
	{
		if (!_items.TryGetValue(key, out KValue value))
			throw new Execution.KernPanic($"ключ {key} отсутствует в словаре");
		return value;
	}

	public void Set(KValue key, KValue value) => _items[key] = value;

	public bool Remove(KValue key) => _items.Remove(key);

	public IEnumerable<KeyValuePair<KValue, KValue>> Items => _items;

	public override string ToString() => $"dict({_items.Count})";
}
