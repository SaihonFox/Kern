using System.Collections.Concurrent;
using Kern.Binding.Attributes;
using Kern.Binding.Marshalling;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Threading;

/// <summary>
/// Потокобезопасный список (spec round 24): каждая операция атомарна. Составные действия
/// делайте через <c>update</c>/<c>add_if_absent</c> или под <c>mutex</c>.
/// </summary>
[KernType("conc_list", GenericArity = 1)]
public sealed class KernConcList
{
	private readonly List<KValue> _items = [];
	private readonly Lock _gate = new();

	public int Count
	{
		get
		{
			lock (_gate) return _items.Count;
		}
	}

	public void Add(KValue value)
	{
		lock (_gate) _items.Add(value);
	}

	public bool AddIfAbsent(KValue value)
	{
		lock (_gate)
		{
			if (_items.Contains(value))
				return false;
			_items.Add(value);
			return true;
		}
	}

	public KValue Get(int index)
	{
		lock (_gate) return _items[Normalize(index)];
	}

	public void Set(int index, KValue value)
	{
		lock (_gate) _items[Normalize(index)] = value;
	}

	public KValue Update(Interpreter interpreter, int index, KValue function)
	{
		lock (_gate)
		{
			KValue next = interpreter.RunValue(function, _items[Normalize(index)]);
			_items[Normalize(index)] = next;
			return next;
		}
	}

	public bool Contains(KValue value)
	{
		lock (_gate) return _items.Contains(value);
	}

	public void RemoveAt(int index)
	{
		lock (_gate) _items.RemoveAt(Normalize(index));
	}

	public bool Remove(KValue value)
	{
		lock (_gate) return _items.Remove(value);
	}

	public void Clear()
	{
		lock (_gate) _items.Clear();
	}

	public KValue Snapshot()
	{
		lock (_gate) return KValue.Obj(new KList(null, [.. _items]));
	}

	private int Normalize(int index) => index < 0 ? index + _items.Count : index;
}

/// <summary>Фабрика и члены <c>conc_list&lt;T&gt;</c>.</summary>
[KernType("conc_list", GenericArity = 1)]
public static class ConcListApi
{
	[KernCtor]
	[KernName("conc_list")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernConcList()));

	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	[KernMember]
	public static void Add(KNativeBox self, KValue value) => Require(self).Add(value);

	[KernName("add_if_absent")]
	[KernMember]
	public static bool AddIfAbsent(KNativeBox self, KValue value) => Require(self).AddIfAbsent(value);

	[KernMember]
	[KernReturn("T")]
	public static KValue Get(KNativeBox self, int index) => Require(self).Get(index);

	[KernMember]
	public static void Set(KNativeBox self, int index, KValue value) => Require(self).Set(index, value);

	[KernMember]
	[KernReturn("T")]
	public static KValue Update(Interpreter interpreter, KNativeBox self, int index, KValue function) =>
		Require(self).Update(interpreter, index, function);

	[KernMember]
	public static bool Contains(KNativeBox self, KValue value) => Require(self).Contains(value);

	[KernName("remove_at")]
	[KernMember]
	public static void RemoveAt(KNativeBox self, int index) => Require(self).RemoveAt(index);

	[KernMember]
	public static bool Remove(KNativeBox self, KValue value) => Require(self).Remove(value);

	[KernMember]
	public static void Clear(KNativeBox self) => Require(self).Clear();

	[KernMember]
	[KernReturn("list<T>")]
	public static KValue Snapshot(KNativeBox self) => Require(self).Snapshot();

	private static KernConcList Require(KNativeBox self) => self.Value switch
	{
		KernConcList list => list,
		_ => throw new KernPanic($"conc_list: ожидался потокобезопасный список, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Потокобезопасный словарь (spec round 24): операции атомарны.</summary>
[KernType("conc_dict", GenericArity = 2)]
public sealed class KernConcDict
{
	private readonly Dictionary<KValue, KValue> _items = [];
	private readonly Lock _gate = new();

	public int Count
	{
		get
		{
			lock (_gate) return _items.Count;
		}
	}

	public KValue Get(KValue key)
	{
		lock (_gate) return _items.TryGetValue(key, out KValue value) ? value : KValue.Nil;
	}

	public void Set(KValue key, KValue value)
	{
		lock (_gate) _items[key] = value;
	}

	public bool Has(KValue key)
	{
		lock (_gate) return _items.ContainsKey(key);
	}

	public bool TryAdd(KValue key, KValue value)
	{
		lock (_gate) return _items.TryAdd(key, value);
	}

	public bool Remove(KValue key)
	{
		lock (_gate) return _items.Remove(key);
	}

	public void Clear()
	{
		lock (_gate) _items.Clear();
	}

	public KValue GetOrAdd(Interpreter interpreter, KValue key, KValue make)
	{
		lock (_gate)
		{
			if (_items.TryGetValue(key, out KValue existing))
				return existing;
			KValue created = interpreter.RunValue(make);
			_items[key] = created;
			return created;
		}
	}

	public KValue Update(Interpreter interpreter, KValue key, KValue seed, KValue function)
	{
		lock (_gate)
		{
			KValue current = _items.TryGetValue(key, out KValue existing) ? existing : seed;
			KValue next = interpreter.RunValue(function, current);
			_items[key] = next;
			return next;
		}
	}

	public KValue Keys()
	{
		lock (_gate) return KValue.Obj(new KList(null, [.. _items.Keys]));
	}

	public KValue Values()
	{
		lock (_gate) return KValue.Obj(new KList(null, [.. _items.Values]));
	}
}

/// <summary>Фабрика и члены <c>conc_dict&lt;K, V&gt;</c>.</summary>
[KernType("conc_dict", GenericArity = 2)]
public static class ConcDictApi
{
	[KernCtor]
	[KernName("conc_dict")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernConcDict()));

	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	[KernMember]
	[KernReturn("T2?")]
	public static KValue Get(KNativeBox self, KValue key) => Require(self).Get(key);

	[KernMember]
	public static void Set(KNativeBox self, KValue key, KValue value) => Require(self).Set(key, value);

	[KernMember]
	public static bool Has(KNativeBox self, KValue key) => Require(self).Has(key);

	[KernName("try_add")]
	[KernMember]
	public static bool TryAdd(KNativeBox self, KValue key, KValue value) => Require(self).TryAdd(key, value);

	[KernMember]
	public static bool Remove(KNativeBox self, KValue key) => Require(self).Remove(key);

	[KernMember]
	public static void Clear(KNativeBox self) => Require(self).Clear();

	[KernName("get_or_add")]
	[KernMember]
	[KernReturn("T2")]
	public static KValue GetOrAdd(Interpreter interpreter, KNativeBox self, KValue key, KValue make) =>
		Require(self).GetOrAdd(interpreter, key, make);

	[KernMember]
	[KernReturn("T2")]
	public static KValue Update(Interpreter interpreter, KNativeBox self, KValue key, KValue seed, KValue function) =>
		Require(self).Update(interpreter, key, seed, function);

	[KernMember]
	[KernReturn("list<T1>")]
	public static KValue Keys(KNativeBox self) => Require(self).Keys();

	[KernMember]
	[KernReturn("list<T2>")]
	public static KValue Values(KNativeBox self) => Require(self).Values();

	private static KernConcDict Require(KNativeBox self) => self.Value switch
	{
		KernConcDict dictionary => dictionary,
		_ => throw new KernPanic($"conc_dict: ожидался потокобезопасный словарь, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Потокобезопасная FIFO-очередь (spec round 24).</summary>
[KernType("conc_queue", GenericArity = 1)]
public sealed class KernConcQueue
{
	private readonly ConcurrentQueue<KValue> _queue = new();

	public int Count => _queue.Count;

	public bool IsEmpty => _queue.IsEmpty;

	public void Push(KValue value) => _queue.Enqueue(value);

	public KValue Pop() => _queue.TryDequeue(out KValue value) ? value : KValue.Nil;

	public KValue Peek() => _queue.TryPeek(out KValue value) ? value : KValue.Nil;
}

/// <summary>Фабрика и члены <c>conc_queue&lt;T&gt;</c>.</summary>
[KernType("conc_queue", GenericArity = 1)]
public static class ConcQueueApi
{
	[KernCtor]
	[KernName("conc_queue")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernConcQueue()));

	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KNativeBox self) => Require(self).IsEmpty;

	[KernMember]
	public static void Push(KNativeBox self, KValue value) => Require(self).Push(value);

	[KernMember]
	[KernReturn("T?")]
	public static KValue Pop(KNativeBox self) => Require(self).Pop();

	[KernMember]
	[KernReturn("T?")]
	public static KValue Peek(KNativeBox self) => Require(self).Peek();

	private static KernConcQueue Require(KNativeBox self) => self.Value switch
	{
		KernConcQueue queue => queue,
		_ => throw new KernPanic($"conc_queue: ожидалась потокобезопасная очередь, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>
/// Общая ячейка <c>shared&lt;T&gt;</c> (spec round 24): одно значение, безопасное для
/// чтения и записи из разных потоков.
/// </summary>
[KernType("shared", GenericArity = 1)]
public sealed class KernShared(KValue initial)
{
	private KValue _value = initial;
	private readonly Lock _gate = new();

	public KValue Value
	{
		get
		{
			lock (_gate) return _value;
		}
		set
		{
			lock (_gate) _value = value;
		}
	}

	public KValue Swap(KValue value)
	{
		lock (_gate)
		{
			KValue previous = _value;
			_value = value;
			return previous;
		}
	}

	public KValue Update(Interpreter interpreter, KValue function)
	{
		lock (_gate)
		{
			_value = interpreter.RunValue(function, _value);
			return _value;
		}
	}
}

/// <summary>Фабрика и члены <c>shared&lt;T&gt;</c>.</summary>
[KernType("shared", GenericArity = 1)]
public static class SharedApi
{
	[KernCtor]
	[KernName("shared")]
	public static KValue Create(KValue value) => KValue.Obj(new KNativeBox(new KernShared(value)));

	/// <summary><c>value</c>: чтение общей ячейки.</summary>
	[KernProperty]
	[KernName("value")]
	[KernReturn("T")]
	public static KValue GetValue(KNativeBox self) => Require(self).Value;

	/// <summary><c>value = x</c>: запись общей ячейки.</summary>
	[KernProperty]
	[KernName("value")]
	public static void SetValue(KNativeBox self, KValue value) => Require(self).Value = value;

	[KernMember]
	[KernReturn("T")]
	public static KValue Get(KNativeBox self) => Require(self).Value;

	[KernMember]
	public static void Set(KNativeBox self, KValue value) => Require(self).Value = value;

	[KernMember]
	[KernReturn("T")]
	public static KValue Swap(KNativeBox self, KValue value) => Require(self).Swap(value);

	[KernMember]
	[KernReturn("T")]
	public static KValue Update(Interpreter interpreter, KNativeBox self, KValue function) =>
		Require(self).Update(interpreter, function);

	private static KernShared Require(KNativeBox self) => self.Value switch
	{
		KernShared shared => shared,
		_ => throw new KernPanic($"shared: ожидалась общая ячейка, получено {self.Value.GetType().Name}"),
	};
}
