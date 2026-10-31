using Kern.Vm.Execution;
using Kern.Vm.Types;

namespace Kern.Vm.Values;

/// <summary>Кортеж фиксированной арности; элементы хранятся по значению.</summary>
public sealed class KTuple
{
	private readonly KValue[] _items;

	public KTuple(KType[] elementTypes, KValue[] items)
	{
		ElementTypes = elementTypes ?? throw new ArgumentNullException(nameof(elementTypes));
		_items = items ?? throw new ArgumentNullException(nameof(items));
		if (ElementTypes.Length != _items.Length)
			throw new KernPanic($"кортеж: типов {ElementTypes.Length}, значений {_items.Length}");
	}

	public KType[] ElementTypes { get; }

	public int Count => _items.Length;

	public KValue this[int index]
	{
		get
		{
			if ((uint)index >= (uint)_items.Length)
				throw new KernPanic($"индекс {index} вне кортежа (длина {_items.Length})");
			return _items[index];
		}
	}

	public ReadOnlySpan<KValue> Items => _items;

	public override string ToString() => $"tuple({_items.Length})";
}
