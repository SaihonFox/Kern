using System.Collections;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Binding.Values;

/// <summary>Типизированное представление Kern <c>dict&lt;K,V&gt;</c>.</summary>
public readonly struct KDict<K, V> : IEnumerable<KeyValuePair<K, V>>
	where K : notnull
{
	public KDict(KValue value)
	{
		if (value.Ref is not Kern.Vm.Values.KDict)
			throw new KernPanic($"KDict<{typeof(K).Name},{typeof(V).Name}>: ожидался dict, получено {value.TypeName}");
		Value = value;
	}

	public KValue Value { get; }

	public Kern.Vm.Values.KDict Raw =>
		Value.Ref as Kern.Vm.Values.KDict ?? throw new KernPanic("KDict: значение не является словарём");

	public int Count => Raw.Count;

	public V this[K key]
	{
		get => Marshalling.KMarshal.To<V>(Raw.Get(Marshalling.KMarshal.From(key)));
		set => Raw.Set(Marshalling.KMarshal.From(key), Marshalling.KMarshal.From(value));
	}

	public bool ContainsKey(K key) => Raw.TryGet(Marshalling.KMarshal.From(key), out _);

	public bool TryGetValue(K key, out V value)
	{
		if (Raw.TryGet(Marshalling.KMarshal.From(key), out KValue found))
		{
			value = Marshalling.KMarshal.To<V>(found);
			return true;
		}

		value = default!;
		return false;
	}

	public void Add(K key, V value) => Raw.Set(Marshalling.KMarshal.From(key), Marshalling.KMarshal.From(value));

	public bool Remove(K key) => Raw.Remove(Marshalling.KMarshal.From(key));

	public IEnumerator<KeyValuePair<K, V>> GetEnumerator()
	{
		foreach (var pair in Raw.Items)
			yield return new KeyValuePair<K, V>(
				Marshalling.KMarshal.To<K>(pair.Key),
				Marshalling.KMarshal.To<V>(pair.Value));
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	public static implicit operator KValue(KDict<K, V> dict) => dict.Value;

	public override string ToString() => Raw.ToString();
}
