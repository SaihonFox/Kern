using System.Collections;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Binding.Values;

/// <summary>
/// Типизированное представление Kern <c>list&lt;T&gt;</c> для нативных привязок
/// (spec §12.5). Это struct-обёртка над VM-значением: копирование не копирует список.
/// </summary>
public readonly struct KList<T> : IEnumerable<T>
{
	public KList(KValue value)
	{
		if (value.Ref is not Kern.Vm.Values.KList)
			throw new KernPanic($"KList<{typeof(T).Name}>: ожидался list, получено {value.TypeName}");
		Value = value;
	}

	public KValue Value { get; }

	public Kern.Vm.Values.KList Raw =>
		Value.Ref as Kern.Vm.Values.KList ?? throw new KernPanic("KList: значение не является списком");

	public int Count => Raw.Count;

	public T this[int index]
	{
		get => Marshalling.KMarshal.To<T>(Raw[index]);
		set => Raw[index] = Marshalling.KMarshal.From(value);
	}

	public void Add(T item) => Raw.Add(Marshalling.KMarshal.From(item));

	public T[] ToArray()
	{
		var result = new T[Raw.Count];
		for (var index = 0; index < result.Length; index++)
			result[index] = Marshalling.KMarshal.To<T>(Raw[index]);
		return result;
	}

	public IEnumerator<T> GetEnumerator()
	{
		foreach (KValue item in Raw.Items)
			yield return Marshalling.KMarshal.To<T>(item);
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	public static implicit operator KValue(KList<T> list) => list.Value;

	public override string ToString() => Raw.ToString();
}
