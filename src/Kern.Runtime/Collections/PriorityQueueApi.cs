using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Очередь с приоритетом <c>priority_queue&lt;T&gt;</c> (spec §16): максимум на вершине.
/// Внутри — двоичная куча в массиве; порядок задаёт <see cref="KernValueOrdering"/>.
/// </summary>
[KernType("priority_queue", GenericArity = 1)]
public sealed class KernPriorityQueue : IKernCollection
{
	private readonly List<KValue> _heap = [];

	/// <summary>Число элементов.</summary>
	public int Count => _heap.Count;

	/// <summary>Элементы кучи (порядок хранения, не порядок приоритета).</summary>
	public IEnumerable<KValue> Items => _heap;

	/// <summary>Есть ли значение (обычное равенство <see cref="KValue"/>).</summary>
	public bool Contains(KValue item) => _heap.Contains(item);

	/// <summary>Добавить значение; куча поднимает его к корню.</summary>
	public void Push(KValue item, Interpreter interpreter)
	{
		_heap.Add(item);
		int index = _heap.Count - 1;
		while (index > 0)
		{
			int parent = (index - 1) / 2;
			if (KernValueOrdering.Compare(_heap[parent], _heap[index], interpreter) >= 0)
				break;
			(_heap[parent], _heap[index]) = (_heap[index], _heap[parent]);
			index = parent;
		}
	}

	/// <summary>Забрать максимум; на пустой очереди — паника.</summary>
	public KValue Pop(Interpreter interpreter)
	{
		if (_heap.Count == 0)
			throw new KernPanic("pop: пустая очередь с приоритетом");

		KValue top = _heap[0];
		KValue last = _heap[^1];
		_heap.RemoveAt(_heap.Count - 1);
		if (_heap.Count == 0)
			return top;

		_heap[0] = last;
		int index = 0;
		while (true)
		{
			int left = index * 2 + 1;
			int right = left + 1;
			int largest = index;
			if (left < _heap.Count &&
				KernValueOrdering.Compare(_heap[left], _heap[largest], interpreter) > 0)
			{
				largest = left;
			}
			if (right < _heap.Count &&
				KernValueOrdering.Compare(_heap[right], _heap[largest], interpreter) > 0)
			{
				largest = right;
			}
			if (largest == index)
				break;
			(_heap[largest], _heap[index]) = (_heap[index], _heap[largest]);
			index = largest;
		}

		return top;
	}

	/// <summary>Максимум без извлечения; <c>nil</c>, если очередь пуста.</summary>
	public KValue Peek() => _heap.Count == 0 ? KValue.Nil : _heap[0];

	/// <summary>Очистить очередь.</summary>
	public void Clear() => _heap.Clear();

	/// <summary>Снимок элементов в порядке приоритета (максимум первым).</summary>
	public KList ToList(Interpreter interpreter)
	{
		var items = new List<KValue>(_heap);
		items.Sort((a, b) => KernValueOrdering.Compare(b, a, interpreter));
		return new KList(null, items);
	}

	public override string ToString() => $"priority_queue({_heap.Count})";
}

/// <summary>
/// Члены и фабрика <c>priority_queue&lt;T&gt;</c> (spec §16): <c>push</c>, <c>pop</c>,
/// <c>peek</c>, <c>clear</c>, свойства <c>len</c>/<c>is_empty</c>, <c>to_list</c>
/// (в порядке приоритета).
/// </summary>
[KernType("priority_queue", GenericArity = 1)]
public static class PriorityQueueApi
{
	/// <summary><c>priority_queue&lt;T&gt;()</c>: пустая очередь (максимум сверху).</summary>
	[KernCtor]
	[KernName("priority_queue")]
	[KernReturn("priority_queue<T>")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernPriorityQueue()));

	/// <summary><c>len</c>: число элементов.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>is_empty</c>: очередь пуста.</summary>
	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KNativeBox self) => Require(self).Count == 0;

	/// <summary><c>push(v)</c>: добавить значение.</summary>
	[KernMember]
	public static void Push(Interpreter interpreter, KNativeBox self, KValue item) =>
		Require(self).Push(item, interpreter);

	/// <summary><c>pop() -&gt; T</c>: забрать максимум.</summary>
	[KernMember]
	[KernReturn("T")]
	public static KValue Pop(Interpreter interpreter, KNativeBox self) => Require(self).Pop(interpreter);

	/// <summary><c>peek() -&gt; T?</c>: максимум без извлечения.</summary>
	[KernMember]
	[KernReturn("T?")]
	public static KValue Peek(KNativeBox self) => Require(self).Peek();

	/// <summary><c>clear()</c>: очистить очередь.</summary>
	[KernMember]
	public static void Clear(KNativeBox self) => Require(self).Clear();

	/// <summary><c>to_list() -&gt; list&lt;T&gt;</c>: элементы в порядке приоритета.</summary>
	[KernName("to_list")]
	[KernMember]
	[KernReturn("list<T>")]
	public static KValue ToList(Interpreter interpreter, KNativeBox self) =>
		KValue.Obj(Require(self).ToList(interpreter));

	private static KernPriorityQueue Require(KNativeBox self) => self.Value switch
	{
		KernPriorityQueue queue => queue,
		_ => throw new KernPanic($"ожидалась priority_queue, получено {self.Value.GetType().Name}"),
	};
}
