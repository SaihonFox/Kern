using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections.Containers;

/// <summary>Очередь FIFO.</summary>
[KernType("queue")]
public sealed class KernQueue<T> : KernCollection<T>
{
	private readonly Queue<KValue> _items = new();

	[KernCtor] public KernQueue() { }

	[KernProperty] public override int Len => _items.Count;
	[KernProperty] public override bool IsEmpty => _items.Count == 0;

	[KernMember] public void Enqueue(T item) => _items.Enqueue(Kv.Of(item));

	/// <summary>Первый элемент или nil, если очередь пуста.</summary>
	[KernMember] public T? Dequeue() => _items.TryDequeue(out var v) ? Kv.As<T>(v) : Kv.As<T>(KValue.Nil);

	[KernMember] public T? Peek() => _items.TryPeek(out var v) ? Kv.As<T>(v) : Kv.As<T>(KValue.Nil);

	[KernMember] public override void Clear() => _items.Clear();

	/// <summary>Для `loop var x in queue`: от головы к хвосту.</summary>
	[KernMember] public override KList<T> Iter() => Kv.List<T>(_items);
}
