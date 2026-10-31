using Kern.Vm.Execution;
using Kern.Vm.Types;

namespace Kern.Vm.Values;

/// <summary>Список <c>list&lt;T&gt;</c>: изменяемый, элементы — <see cref="KValue"/>.</summary>
public sealed class KList
{
	public KList(KType? elementType = null, IEnumerable<KValue>? items = null)
	{
		ElementType = elementType;
		Items = items is null ? [] : [.. items];
	}

	/// <summary>Reified-тип элемента; <c>null</c> — тип не сохранён (пустой литерал без вывода).</summary>
	public KType? ElementType { get; }

	public List<KValue> Items { get; }

	public int Count => Items.Count;

	public KValue this[int index]
	{
		get
		{
			if ((uint)index >= (uint)Items.Count)
				throw new KernPanic($"индекс {index} вне списка (длина {Items.Count})");
			return Items[index];
		}
		set
		{
			if ((uint)index >= (uint)Items.Count)
				throw new KernPanic($"индекс {index} вне списка (длина {Items.Count})");
			Items[index] = value;
		}
	}

	public void Add(KValue value) => Items.Add(value);

	public override string ToString() => $"list<{ElementType?.Canonical ?? "object"}>({Items.Count})";
}
