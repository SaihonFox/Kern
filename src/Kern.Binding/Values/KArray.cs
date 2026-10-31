using System.Collections;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Binding.Values;

/// <summary>Типизированное представление Kern <c>array&lt;T&gt;</c> (фиксированная длина).</summary>
public readonly struct KArray<T> : IEnumerable<T>
{
	public KArray(KValue value)
	{
		if (value.Ref is not Kern.Vm.Values.KArray)
			throw new KernPanic($"KArray<{typeof(T).Name}>: ожидался array, получено {value.TypeName}");
		Value = value;
	}

	public KValue Value { get; }

	public Kern.Vm.Values.KArray Raw =>
		Value.Ref as Kern.Vm.Values.KArray ?? throw new KernPanic("KArray: значение не является массивом");

	public int Length => Raw.Length;

	public int Count => Raw.Length;

	public T this[int index]
	{
		get => Marshalling.KMarshal.To<T>(Raw[index]);
		set => Raw[index] = Marshalling.KMarshal.From(value);
	}

	public T[] ToArray()
	{
		var result = new T[Raw.Length];
		for (var index = 0; index < result.Length; index++)
			result[index] = Marshalling.KMarshal.To<T>(Raw[index]);
		return result;
	}

	public IEnumerator<T> GetEnumerator()
	{
		for (var index = 0; index < Raw.Length; index++)
			yield return Marshalling.KMarshal.To<T>(Raw[index]);
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	public static implicit operator KValue(KArray<T> array) => array.Value;

	public override string ToString() => Raw.ToString();
}
