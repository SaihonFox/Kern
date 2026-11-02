using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Кольцевой буфер <c>ring_buffer&lt;T&gt;</c> (spec §16): очередь фиксированной ёмкости;
/// <c>push</c> в заполненный буфер затирает самый старый элемент. Внутри — массив,
/// голова и число элементов; порядок обхода — от старейшего к новейшему.
/// </summary>
[KernType("ring_buffer", GenericArity = 1)]
public sealed class KernRingBuffer : IKernCollection
{
	private readonly KValue[] _items;
	private int _head;
	private int _count;

	/// <summary>Создать буфер ёмкости <paramref name="capacity"/>.</summary>
	public KernRingBuffer(int capacity)
	{
		if (capacity <= 0)
			throw new KernPanic($"ring_buffer: ёмкость должна быть положительной, получено {capacity}");
		_items = new KValue[capacity];
	}

	/// <summary>Ёмкость буфера.</summary>
	public int Capacity => _items.Length;

	/// <summary>Число элементов.</summary>
	public int Count => _count;

	/// <summary>Элементы от старейшего к новейшему.</summary>
	public IEnumerable<KValue> Items
	{
		get
		{
			for (var offset = 0; offset < _count; offset++)
				yield return _items[(_head + offset) % _items.Length];
		}
	}

	/// <summary>Есть ли элемент (обычное равенство <see cref="KValue"/>).</summary>
	public bool Contains(KValue item) => Items.Contains(item);

	/// <summary>Добавить значение; заполненный буфер затирает старейший элемент.</summary>
	public void Push(KValue item)
	{
		if (_count < _items.Length)
		{
			_items[(_head + _count) % _items.Length] = item;
			_count++;
			return;
		}

		_items[_head] = item;
		_head = (_head + 1) % _items.Length;
	}

	/// <summary>Забрать старейший элемент; <c>nil</c>, если буфер пуст.</summary>
	public KValue Pop()
	{
		if (_count == 0)
			return KValue.Nil;
		KValue item = _items[_head];
		_head = (_head + 1) % _items.Length;
		_count--;
		return item;
	}

	/// <summary>Старейший элемент без извлечения; <c>nil</c>, если буфер пуст.</summary>
	public KValue Peek() => _count == 0 ? KValue.Nil : _items[_head];

	/// <summary>Очистить буфер.</summary>
	public void Clear()
	{
		Array.Clear(_items);
		_head = 0;
		_count = 0;
	}

	/// <summary>Снимок элементов от старейшего к новейшему.</summary>
	public KList ToList() => new(null, Items);

	public override string ToString() => $"ring_buffer({_count}/{_items.Length})";
}

/// <summary>
/// Фабрика и члены <c>ring_buffer&lt;T&gt;</c> (spec §16): <c>push(v)</c> (перезаписывает
/// старейший), <c>pop() -&gt; T?</c>, <c>peek() -&gt; T?</c>, свойства <c>len</c>/<c>capacity</c>/
/// <c>is_empty</c>, <c>clear()</c>, <c>to_list()</c>.
/// </summary>
[KernType("ring_buffer", GenericArity = 1)]
public static class RingBufferApi
{
	/// <summary><c>ring_buffer(capacity)</c>: пустой буфер заданной ёмкости.</summary>
	[KernCtor]
	[KernName("ring_buffer")]
	[KernReturn("ring_buffer<T>")]
	public static KValue Create(int capacity) => KValue.Obj(new KNativeBox(new KernRingBuffer(capacity)));

	/// <summary><c>len</c>: число элементов.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>capacity</c>: ёмкость буфера.</summary>
	[KernProperty]
	public static int Capacity(KNativeBox self) => Require(self).Capacity;

	/// <summary><c>is_empty</c>: буфер пуст.</summary>
	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KNativeBox self) => Require(self).Count == 0;

	/// <summary><c>push(v)</c>: добавить; заполненный буфер затирает старейший.</summary>
	[KernMember]
	public static void Push(KNativeBox self, KValue item) => Require(self).Push(item);

	/// <summary><c>pop() -&gt; T?</c>: забрать старейший.</summary>
	[KernMember]
	[KernReturn("T?")]
	public static KValue Pop(KNativeBox self) => Require(self).Pop();

	/// <summary><c>peek() -&gt; T?</c>: старейший без извлечения.</summary>
	[KernMember]
	[KernReturn("T?")]
	public static KValue Peek(KNativeBox self) => Require(self).Peek();

	/// <summary><c>clear()</c>: очистить буфер.</summary>
	[KernMember]
	public static void Clear(KNativeBox self) => Require(self).Clear();

	/// <summary><c>to_list() -&gt; list&lt;T&gt;</c>: от старейшего к новейшему.</summary>
	[KernName("to_list")]
	[KernMember]
	[KernReturn("list<T>")]
	public static KValue ToList(KNativeBox self) => KValue.Obj(Require(self).ToList());

	private static KernRingBuffer Require(KNativeBox self) => self.Value switch
	{
		KernRingBuffer buffer => buffer,
		_ => throw new KernPanic($"ожидался ring_buffer, получено {self.Value.GetType().Name}"),
	};
}
