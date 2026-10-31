using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections.Containers;

/// <summary>Стек LIFO.</summary>
[KernType("stack")]
public sealed class KernStack<T> : KernCollection<T>
{
	private readonly Stack<KValue> _items = new();

	[KernCtor] public KernStack() { }

	[KernProperty] public override int Len => _items.Count;
	[KernProperty] public override bool IsEmpty => _items.Count == 0;

	[KernMember] public void Push(T item) => _items.Push(Kv.Of(item));

	/// <summary>Верхний элемент или nil, если стек пуст.</summary>
	[KernMember] public T? Pop() => _items.TryPop(out var v) ? Kv.As<T>(v) : Kv.As<T>(KValue.Nil);

	[KernMember] public T? Peek() => _items.TryPeek(out var v) ? Kv.As<T>(v) : Kv.As<T>(KValue.Nil);

	[KernMember] public override void Clear() => _items.Clear();

	/// <summary>Для `loop var x in stack`: от вершины к основанию.</summary>
	[KernMember] public override KList<T> Iter() => Kv.List<T>(_items);
}
