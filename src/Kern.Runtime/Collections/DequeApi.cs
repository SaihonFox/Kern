using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Двусторонняя очередь <c>deque&lt;T&gt;</c> (spec §16): добавление и извлечение с обоих
/// концов. Значение живёт в VM внутри <see cref="KNativeBox"/>; члены — в <see cref="DequeApi"/>.
/// Реализует общий протокол <c>collection&lt;T&gt;</c>.
/// </summary>
[KernType("deque", GenericArity = 1)]
public sealed class KernDeque : IKernCollection
{
	private readonly List<KValue> _items = [];

	/// <summary>Число элементов.</summary>
	public int Count => _items.Count;

	/// <summary>Элементы от начала к концу.</summary>
	public IEnumerable<KValue> Items => _items;

	/// <summary>Есть ли элемент (по равенству <see cref="KValue"/>).</summary>
	public bool Contains(KValue item) => _items.Contains(item);

	/// <summary>Положить значение в начало.</summary>
	public void PushFront(KValue item) => _items.Insert(0, item);

	/// <summary>Положить значение в конец.</summary>
	public void PushBack(KValue item) => _items.Add(item);

	/// <summary>Забрать значение из начала; на пустой очереди — паника.</summary>
	public KValue PopFront()
	{
		if (_items.Count == 0)
			throw new KernPanic("pop_front: пустая очередь");
		KValue item = _items[0];
		_items.RemoveAt(0);
		return item;
	}

	/// <summary>Забрать значение с конца; на пустой очереди — паника.</summary>
	public KValue PopBack()
	{
		if (_items.Count == 0)
			throw new KernPanic("pop_back: пустая очередь");
		KValue item = _items[^1];
		_items.RemoveAt(_items.Count - 1);
		return item;
	}

	/// <summary>Начало без извлечения; <c>nil</c>, если очередь пуста.</summary>
	public KValue PeekFront() => _items.Count == 0 ? KValue.Nil : _items[0];

	/// <summary>Конец без извлечения; <c>nil</c>, если очередь пуста.</summary>
	public KValue PeekBack() => _items.Count == 0 ? KValue.Nil : _items[^1];

	/// <summary>Очистить очередь.</summary>
	public void Clear() => _items.Clear();

	/// <summary>Копия элементов от начала к концу как <c>list&lt;T&gt;</c>.</summary>
	public KList ToList() => new(null, _items);

	public override string ToString() => $"deque({_items.Count})";
}

/// <summary>
/// Члены и фабрика Kern-типа <c>deque&lt;T&gt;</c> (spec §16): <c>push_front/push_back</c>,
/// <c>pop_front/pop_back</c>, <c>peek_front/peek_back</c>, <c>clear</c>, свойства
/// <c>len</c>/<c>is_empty</c>, <c>to_list</c>.
/// </summary>
[KernType("deque", GenericArity = 1)]
public static class DequeApi
{
	/// <summary><c>deque&lt;T&gt;()</c>: пустая двусторонняя очередь.</summary>
	[KernCtor]
	[KernName("deque")]
	[KernReturn("deque<T>")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernDeque()));

	/// <summary><c>len</c>: число элементов.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>is_empty</c>: очередь пуста.</summary>
	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KNativeBox self) => Require(self).Count == 0;

	/// <summary><c>push_front(v)</c>: добавить в начало.</summary>
	[KernName("push_front")]
	[KernMember]
	public static void PushFront(KNativeBox self, KValue item) => Require(self).PushFront(item);

	/// <summary><c>push_back(v)</c>: добавить в конец.</summary>
	[KernName("push_back")]
	[KernMember]
	public static void PushBack(KNativeBox self, KValue item) => Require(self).PushBack(item);

	/// <summary><c>pop_front() -&gt; T</c>: забрать из начала.</summary>
	[KernName("pop_front")]
	[KernMember]
	[KernReturn("T")]
	public static KValue PopFront(KNativeBox self) => Require(self).PopFront();

	/// <summary><c>pop_back() -&gt; T</c>: забрать с конца.</summary>
	[KernName("pop_back")]
	[KernMember]
	[KernReturn("T")]
	public static KValue PopBack(KNativeBox self) => Require(self).PopBack();

	/// <summary><c>peek_front() -&gt; T?</c>: начало без извлечения.</summary>
	[KernName("peek_front")]
	[KernMember]
	[KernReturn("T?")]
	public static KValue PeekFront(KNativeBox self) => Require(self).PeekFront();

	/// <summary><c>peek_back() -&gt; T?</c>: конец без извлечения.</summary>
	[KernName("peek_back")]
	[KernMember]
	[KernReturn("T?")]
	public static KValue PeekBack(KNativeBox self) => Require(self).PeekBack();

	/// <summary><c>clear()</c>: очистить очередь.</summary>
	[KernMember]
	public static void Clear(KNativeBox self) => Require(self).Clear();

	/// <summary><c>to_list() -&gt; list&lt;T&gt;</c>: элементы от начала к концу.</summary>
	[KernName("to_list")]
	[KernMember]
	[KernReturn("list<T>")]
	public static KValue ToList(KNativeBox self) => KValue.Obj(Require(self).ToList());

	private static KernDeque Require(KNativeBox self) => self.Value switch
	{
		KernDeque deque => deque,
		_ => throw new KernPanic($"ожидался deque, получено {self.Value.GetType().Name}"),
	};
}
