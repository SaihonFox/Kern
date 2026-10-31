using System.Collections.Concurrent;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Threading;

/// <summary>
/// Потокобезопасный список: каждая операция атомарна. Составные действия («проверить и добавить»)
/// делайте через `update` / `add_if_absent` или под mutex.
/// </summary>
[KernType("conc_list")]
public sealed class KernConcList<T>
{
	private readonly List<KValue> _items = [];
	private readonly object _gate = new();

	[KernCtor] public KernConcList() { }

	[KernProperty] public int Len { get { lock (_gate) return _items.Count; } }

	[KernMember] public void Add(T value) { lock (_gate) _items.Add(Kv.Of(value)); }

	/// <summary>Добавить, если такого значения ещё нет; true — добавлено.</summary>
	[KernMember]
	public bool AddIfAbsent(T value)
	{
		var v = Kv.Of(value);
		lock (_gate)
		{
			if (_items.Contains(v, KValueComparer.Instance)) return false;
			_items.Add(v);
			return true;
		}
	}

	[KernMember] public T Get(int index) { lock (_gate) return Kv.As<T>(_items[index]); }

	[KernMember] public void Set(int index, T value) { lock (_gate) _items[index] = Kv.Of(value); }

	/// <summary>Атомарно заменить элемент результатом функции: `l.update(i, fun(i32 x) -> i32 x + 1)`; возвращает новое значение.</summary>
	[KernMember]
	public T Update(int index, KFunc<T, T> f)
	{
		lock (_gate)
		{
			var nv = f.Invoke(_items[index]);
			_items[index] = nv;
			return Kv.As<T>(nv);
		}
	}

	[KernMember] public bool Contains(T value) { lock (_gate) return _items.Contains(Kv.Of(value), KValueComparer.Instance); }

	[KernMember] public void RemoveAt(int index) { lock (_gate) _items.RemoveAt(index); }

	/// <summary>Удалить первое вхождение; false — не найдено.</summary>
	[KernMember]
	public bool Remove(T value)
	{
		var v = Kv.Of(value);
		lock (_gate)
		{
			int i = _items.FindIndex(x => KValueComparer.Instance.Equals(x, v));
			if (i < 0) return false;
			_items.RemoveAt(i);
			return true;
		}
	}

	[KernMember] public void Clear() { lock (_gate) _items.Clear(); }

	/// <summary>Копия содержимого в обычный список (моментальный снимок): по нему можно итерировать без замка.</summary>
	[KernMember]
	public KList<T> Snapshot()
	{
		lock (_gate) return new(new KListObject(TypeFactory.Object, [.. _items]));
	}
}

/// <summary>Потокобезопасный словарь. Операции атомарны; `update` и `get_or_add` выполняются под замком записи.</summary>
[KernType("conc_dict")]
public sealed class KernConcDict<K, V>
{
	private readonly Dictionary<KValue, KValue> _items = new(KValueComparer.Instance);
	private readonly object _gate = new();

	[KernCtor] public KernConcDict() { }

	[KernProperty] public int Len { get { lock (_gate) return _items.Count; } }

	/// <summary>Значение по ключу или nil.</summary>
	[KernMember]
	public V? Get(K key)
	{
		lock (_gate) return _items.TryGetValue(Kv.Of(key), out var v) ? Kv.As<V>(v) : Kv.As<V>(KValue.Nil);
	}

	[KernMember] public void Set(K key, V value) { lock (_gate) _items[Kv.Of(key)] = Kv.Of(value); }

	[KernMember] public bool Has(K key) { lock (_gate) return _items.ContainsKey(Kv.Of(key)); }

	/// <summary>Добавить, только если ключа нет; true — добавлено.</summary>
	[KernMember] public bool TryAdd(K key, V value) { lock (_gate) return _items.TryAdd(Kv.Of(key), Kv.Of(value)); }

	[KernMember] public bool Remove(K key) { lock (_gate) return _items.Remove(Kv.Of(key)); }

	[KernMember] public void Clear() { lock (_gate) _items.Clear(); }

	/// <summary>Вернуть значение; если ключа нет — вычислить `make()`, сохранить и вернуть (вычисляется один раз).</summary>
	[KernMember]
	public V GetOrAdd(K key, KFunc<V> make)
	{
		var k = Kv.Of(key);
		lock (_gate)
		{
			if (_items.TryGetValue(k, out var v)) return Kv.As<V>(v);
			var nv = make.Invoke();
			_items[k] = nv;
			return Kv.As<V>(nv);
		}
	}

	/// <summary>
	/// Атомарное изменение: `d.update(word, 0, fun(i32 n) -> i32 n + 1)` — если ключа нет, берётся seed.
	/// Возвращает новое значение.
	/// </summary>
	[KernMember]
	public V Update(K key, V seed, KFunc<V, V> f)
	{
		var k = Kv.Of(key);
		lock (_gate)
		{
			var cur = _items.TryGetValue(k, out var v) ? v : Kv.Of(seed);
			var nv = f.Invoke(cur);
			_items[k] = nv;
			return Kv.As<V>(nv);
		}
	}

	[KernMember]
	public KList<K> Keys()
	{
		lock (_gate) return new(new KListObject(TypeFactory.Object, [.. _items.Keys]));
	}

	[KernMember]
	public KList<V> Values()
	{
		lock (_gate) return new(new KListObject(TypeFactory.Object, [.. _items.Values]));
	}
}

/// <summary>Потокобезопасная очередь FIFO без блокировок (для блокирующей передачи используйте `channel`).</summary>
[KernType("conc_queue")]
public sealed class KernConcQueue<T>
{
	private readonly ConcurrentQueue<KValue> _queue = new();

	[KernCtor] public KernConcQueue() { }

	[KernProperty] public int Len => _queue.Count;
	[KernProperty] public bool IsEmpty => _queue.IsEmpty;

	[KernMember] public void Push(T value) => _queue.Enqueue(Kv.Of(value));

	/// <summary>Взять первый элемент; nil — очередь пуста.</summary>
	[KernMember] public T? Pop() => _queue.TryDequeue(out var v) ? Kv.As<T>(v) : Kv.As<T>(KValue.Nil);

	/// <summary>Посмотреть первый элемент, не извлекая; nil — пусто.</summary>
	[KernMember] public T? Peek() => _queue.TryPeek(out var v) ? Kv.As<T>(v) : Kv.As<T>(KValue.Nil);
}

/// <summary>
/// Общая ячейка: одно значение, которое безопасно читать и менять из разных потоков.
/// <code>
/// shared&lt;str&gt; status = shared("idle")
/// status.set("running")
/// status.update(fun(str s) -> str s + "!")
/// </code>
/// </summary>
[KernType("shared")]
public sealed class KernShared<T>
{
	private KValue _value;
	private readonly object _gate = new();

	[KernCtor] public KernShared(T initial) => _value = Kv.Of(initial);

	[KernProperty] public T Value { get { lock (_gate) return Kv.As<T>(_value); } set { lock (_gate) _value = Kv.Of(value); } }

	[KernMember] public T Get() { lock (_gate) return Kv.As<T>(_value); }
	[KernMember] public void Set(T value) { lock (_gate) _value = Kv.Of(value); }

	/// <summary>Записать новое значение и вернуть прежнее.</summary>
	[KernMember]
	public T Swap(T value)
	{
		lock (_gate)
		{
			var old = _value;
			_value = Kv.Of(value);
			return Kv.As<T>(old);
		}
	}

	/// <summary>Атомарно пересчитать значение функцией; возвращает новое значение.</summary>
	[KernMember]
	public T Update(KFunc<T, T> f)
	{
		lock (_gate)
		{
			_value = f.Invoke(_value);
			return Kv.As<T>(_value);
		}
	}
}
