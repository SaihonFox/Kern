using Kern.Vm.Execution;
using Kern.Vm.Types;

namespace Kern.Vm.Values;

/// <summary>Массив фиксированной длины <c>array&lt;T&gt;</c>.</summary>
public sealed class KArray
{
	private readonly KValue[] _items;

	/// <param name="items">Массив передаётся без копирования (вызывающий отдаёт владение).</param>
	public KArray(KType? elementType, KValue[] items)
	{
		ElementType = elementType;
		_items = items ?? throw new ArgumentNullException(nameof(items));
	}

	public KType? ElementType { get; }

	public int Length => _items.Length;

	public KValue this[int index]
	{
		get
		{
			if ((uint)index >= (uint)_items.Length)
				throw new KernPanic($"индекс {index} вне массива (длина {_items.Length})");
			return _items[index];
		}
		set
		{
			if ((uint)index >= (uint)_items.Length)
				throw new KernPanic($"индекс {index} вне массива (длина {_items.Length})");
			_items[index] = value;
		}
	}

	public ReadOnlySpan<KValue> Items => _items;

	public override string ToString() => $"array<{ElementType?.Canonical ?? "object"}>({_items.Length})";
}
