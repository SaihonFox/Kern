using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Множество Kern-типа <c>set&lt;T&gt;</c> (spec §16): уникальные значения в порядке
/// добавления, равенство — по <see cref="KValue"/> (как у <c>list</c>). Значение живёт
/// в VM внутри <see cref="KNativeBox"/>; члены — в <see cref="SetApi"/>.
/// </summary>
[KernType("set", GenericArity = 1)]
public sealed class KernSet
{
	private readonly List<KValue> _items = [];

	/// <summary>Число элементов (без повторов).</summary>
	public int Count => _items.Count;

	/// <summary>Элементы в порядке добавления; менять напрямую нельзя.</summary>
	public IReadOnlyList<KValue> Items => _items;

	/// <summary>Добавить значение; повтор (по равенству <see cref="KValue"/>) игнорируется.</summary>
	public void Add(KValue item)
	{
		if (!_items.Contains(item))
			_items.Add(item);
	}

	/// <summary>Удалить значение; <c>true</c>, если оно было.</summary>
	public bool Remove(KValue item) => _items.Remove(item);

	/// <summary>Есть ли значение.</summary>
	public bool Contains(KValue item) => _items.Contains(item);

	/// <summary>Очистить множество.</summary>
	public void Clear() => _items.Clear();

	/// <summary>Копия элементов как <c>list&lt;T&gt;</c>.</summary>
	public KList ToList() => new(null, _items);

	public override string ToString() => $"set({_items.Count})";
}

/// <summary>
/// Члены и фабрика Kern-типа <c>set&lt;T&gt;</c> (spec §16): <c>add</c>, <c>remove</c>,
/// <c>contains</c>, <c>clear</c>, свойства <c>len</c>/<c>is_empty</c>, <c>to_list</c>.
/// Первый параметр членов — <c>self</c> из <see cref="KNativeBox"/>; элемент приходит как
/// <see cref="KValue"/>, а типом <c>T</c> его делает binder (generic-аргумент получателя).
/// </summary>
[KernType("set", GenericArity = 1)]
public static class SetApi
{
	/// <summary><c>set&lt;T&gt;()</c>: пустое множество.</summary>
	[KernCtor]
	[KernName("set")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernSet()));

	/// <summary><c>len</c>: число элементов.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>is_empty</c>: множество пусто.</summary>
	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KNativeBox self) => Require(self).Count == 0;

	/// <summary><c>add(item)</c>: добавить, повтор игнорируется.</summary>
	[KernMember]
	public static void Add(KNativeBox self, KValue item) => Require(self).Add(item);

	/// <summary><c>remove(item)</c>: удалить; <c>true</c>, если элемент был.</summary>
	[KernMember]
	public static bool Remove(KNativeBox self, KValue item) => Require(self).Remove(item);

	/// <summary><c>contains(item)</c>: есть ли элемент (его же использует <c>in</c>).</summary>
	[KernMember]
	public static bool Contains(KNativeBox self, KValue item) => Require(self).Contains(item);

	/// <summary><c>clear()</c>: очистить множество.</summary>
	[KernMember]
	public static void Clear(KNativeBox self) => Require(self).Clear();

	/// <summary><c>to_list()</c>: элементы в порядке добавления как <c>list&lt;T&gt;</c>.</summary>
	[KernName("to_list")]
	[KernMember]
	public static KValue ToList(KNativeBox self) => KValue.Obj(Require(self).ToList());

	private static KernSet Require(KNativeBox self) => self.Value switch
	{
		KernSet set => set,
		_ => throw new KernPanic($"ожидался set, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>
/// Очередь Kern-типа <c>queue&lt;T&gt;</c> (spec §16): FIFO, добавление в конец,
/// извлечение из начала. Значение живёт в VM внутри <see cref="KNativeBox"/>;
/// члены — в <see cref="QueueApi"/>.
/// </summary>
[KernType("queue", GenericArity = 1)]
public sealed class KernQueue
{
	private readonly List<KValue> _items = [];
	private int _head;

	/// <summary>Число элементов в очереди.</summary>
	public int Count => _items.Count - _head;

	/// <summary>Поставить значение в конец очереди.</summary>
	public void Enqueue(KValue item) => _items.Add(item);

	/// <summary>Забрать значение из начала; на пустой очереди — паника.</summary>
	public KValue Dequeue()
	{
		if (Count == 0)
			throw new KernPanic("dequeue: пустая очередь");

		KValue item = _items[_head++];
		if (_head >= 32 && _head * 2 >= _items.Count)
		{
			_items.RemoveRange(0, _head);
			_head = 0;
		}

		return item;
	}

	/// <summary>Посмотреть начало без извлечения; на пустой очереди — паника.</summary>
	public KValue Peek()
	{
		if (Count == 0)
			throw new KernPanic("peek: пустая очередь");

		return _items[_head];
	}

	/// <summary>Очистить очередь.</summary>
	public void Clear()
	{
		_items.Clear();
		_head = 0;
	}

	/// <summary>Копия элементов от начала к концу как <c>list&lt;T&gt;</c>.</summary>
	public KList ToList() => new(null, _items.Skip(_head));

	public override string ToString() => $"queue({Count})";
}

/// <summary>
/// Члены и фабрика Kern-типа <c>queue&lt;T&gt;</c> (spec §16): <c>enqueue</c>,
/// <c>dequeue</c>, <c>peek</c>, <c>clear</c>, свойства <c>len</c>/<c>is_empty</c>,
/// <c>to_list</c>. Элемент приходит как <see cref="KValue"/>, типом <c>T</c> его
/// делает binder (generic-аргумент получателя).
/// </summary>
[KernType("queue", GenericArity = 1)]
public static class QueueApi
{
	/// <summary><c>queue&lt;T&gt;()</c>: пустая очередь.</summary>
	[KernCtor]
	[KernName("queue")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernQueue()));

	/// <summary><c>len</c>: число элементов.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>is_empty</c>: очередь пуста.</summary>
	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KNativeBox self) => Require(self).Count == 0;

	/// <summary><c>enqueue(item)</c>: поставить в конец.</summary>
	[KernMember]
	public static void Enqueue(KNativeBox self, KValue item) => Require(self).Enqueue(item);

	/// <summary><c>dequeue()</c>: забрать из начала.</summary>
	[KernMember]
	public static KValue Dequeue(KNativeBox self) => Require(self).Dequeue();

	/// <summary><c>peek()</c>: начало без извлечения.</summary>
	[KernMember]
	public static KValue Peek(KNativeBox self) => Require(self).Peek();

	/// <summary><c>clear()</c>: очистить очередь.</summary>
	[KernMember]
	public static void Clear(KNativeBox self) => Require(self).Clear();

	/// <summary><c>to_list()</c>: элементы от начала к концу как <c>list&lt;T&gt;</c>.</summary>
	[KernName("to_list")]
	[KernMember]
	public static KValue ToList(KNativeBox self) => KValue.Obj(Require(self).ToList());

	private static KernQueue Require(KNativeBox self) => self.Value switch
	{
		KernQueue queue => queue,
		_ => throw new KernPanic($"ожидалась queue, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>
/// Стек Kern-типа <c>stack&lt;T&gt;</c> (spec §16): LIFO, добавление и извлечение
/// с конца. Значение живёт в VM внутри <see cref="KNativeBox"/>; члены —
/// в <see cref="StackApi"/>.
/// </summary>
[KernType("stack", GenericArity = 1)]
public sealed class KernStack
{
	private readonly List<KValue> _items = [];

	/// <summary>Число элементов в стеке.</summary>
	public int Count => _items.Count;

	/// <summary>Положить значение на вершину.</summary>
	public void Push(KValue item) => _items.Add(item);

	/// <summary>Снять значение с вершины; на пустом стеке — паника.</summary>
	public KValue Pop()
	{
		if (_items.Count == 0)
			throw new KernPanic("pop: пустой стек");

		KValue item = _items[^1];
		_items.RemoveAt(_items.Count - 1);
		return item;
	}

	/// <summary>Посмотреть вершину без снятия; на пустом стеке — паника.</summary>
	public KValue Peek()
	{
		if (_items.Count == 0)
			throw new KernPanic("peek: пустой стек");

		return _items[^1];
	}

	/// <summary>Очистить стек.</summary>
	public void Clear() => _items.Clear();

	/// <summary>Копия элементов от дна к вершине как <c>list&lt;T&gt;</c>.</summary>
	public KList ToList() => new(null, _items);

	public override string ToString() => $"stack({_items.Count})";
}

/// <summary>
/// Члены и фабрика Kern-типа <c>stack&lt;T&gt;</c> (spec §16): <c>push</c>,
/// <c>pop</c>, <c>peek</c>, <c>clear</c>, свойства <c>len</c>/<c>is_empty</c>,
/// <c>to_list</c>. Элемент приходит как <see cref="KValue"/>, типом <c>T</c> его
/// делает binder (generic-аргумент получателя).
/// </summary>
[KernType("stack", GenericArity = 1)]
public static class StackApi
{
	/// <summary><c>stack&lt;T&gt;()</c>: пустой стек.</summary>
	[KernCtor]
	[KernName("stack")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernStack()));

	/// <summary><c>len</c>: число элементов.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>is_empty</c>: стек пуст.</summary>
	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KNativeBox self) => Require(self).Count == 0;

	/// <summary><c>push(item)</c>: положить на вершину.</summary>
	[KernMember]
	public static void Push(KNativeBox self, KValue item) => Require(self).Push(item);

	/// <summary><c>pop()</c>: снять с вершины.</summary>
	[KernMember]
	public static KValue Pop(KNativeBox self) => Require(self).Pop();

	/// <summary><c>peek()</c>: вершина без снятия.</summary>
	[KernMember]
	public static KValue Peek(KNativeBox self) => Require(self).Peek();

	/// <summary><c>clear()</c>: очистить стек.</summary>
	[KernMember]
	public static void Clear(KNativeBox self) => Require(self).Clear();

	/// <summary><c>to_list()</c>: элементы от дна к вершине как <c>list&lt;T&gt;</c>.</summary>
	[KernName("to_list")]
	[KernMember]
	public static KValue ToList(KNativeBox self) => KValue.Obj(Require(self).ToList());

	private static KernStack Require(KNativeBox self) => self.Value switch
	{
		KernStack stack => stack,
		_ => throw new KernPanic($"ожидался stack, получено {self.Value.GetType().Name}"),
	};
}
