using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Threading;

/// <summary>
/// Канал между потоками (общая память: значения передаются по ссылке, без копирования).
/// <c>send</c> блокируется, если канал ограничен и полон; <c>recv</c> блокируется до значения, после <c>close()</c> и опустошения даёт nil.
/// Передав объект через канал, отправитель и получатель делят его — не меняйте его одновременно без замка.
/// </summary>
[KernType("channel")]
public sealed class KernChannel<T>
{
	private readonly System.Collections.Concurrent.BlockingCollection<KValue> _queue;

	[KernCtor] public KernChannel() : this(0) { }

	/// <summary>capacity ≤ 0 — без ограничения.</summary>
	[KernCtor] public KernChannel(int capacity) =>
		_queue = capacity > 0 ? new(capacity) : new();

	[KernProperty] public int Len => _queue.Count;
	[KernProperty] public bool IsClosed => _queue.IsAddingCompleted;

	/// <summary>Отправить значение; false — канал закрыт.</summary>
	[KernMember] public bool Send(T value)
	{
		try { _queue.Add(Kv.Of(value)); return true; }
		catch (InvalidOperationException) { return false; }
	}

	/// <summary>Получить значение (блокирует поток); nil — канал закрыт и пуст.</summary>
	[KernMember] public T? Recv()
	{
		try { return Kv.As<T>(_queue.Take()); }
		catch (InvalidOperationException) { return Kv.As<T>(KValue.Nil); }
	}

	/// <summary>Получить значение, не блокируясь; nil — сейчас пусто.</summary>
	[KernMember] public T? TryRecv() =>
		_queue.TryTake(out var v) ? Kv.As<T>(v) : Kv.As<T>(KValue.Nil);

	/// <summary>Асинхронный приём: `await ch.recv_async()`; не блокирует цикл событий.</summary>
	[KernMember] public Task<KValue> RecvAsync() => Task.Run(() =>
	{
		try { return _queue.Take(); }
		catch (InvalidOperationException) { return KValue.Nil; }
	});

	/// <summary>Закрыть канал: новые send вернут false, recv дочитает остаток и вернёт nil.</summary>
	[KernMember] public void Close() => _queue.CompleteAdding();
}

/// <summary>
/// Взаимное исключение (реентерабельное). Основной способ — защитник области:
/// <code>
/// mutex m = mutex()
/// fun add(i32 n) {
///     use var g = m.guard()      // замок держится до конца блока
///     total += n
/// }
/// </code>
/// </summary>
[KernType("mutex")]
public sealed class KernMutex
{
	private readonly object _gate = new();

	[KernCtor] public KernMutex() { }

	[KernMember] public void Enter() => Monitor.Enter(_gate);
	[KernMember] public bool TryEnter() => Monitor.TryEnter(_gate);
	[KernMember] public void Exit() => Monitor.Exit(_gate);

	/// <summary>Захватить замок; освобождается при выходе из блока (`use var g = m.guard()`) или `g.dispose()`.</summary>
	[KernMember] public KernLockGuard Guard()
	{
		Monitor.Enter(_gate);
		return new KernLockGuard(() => Monitor.Exit(_gate));
	}

	/// <summary>Захватить замок с ожиданием не дольше ms миллисекунд; nil — не удалось.</summary>
	[KernMember] public KernLockGuard? TryGuard(int ms) =>
		Monitor.TryEnter(_gate, ms) ? new KernLockGuard(() => Monitor.Exit(_gate)) : null;

	/// <summary>Выполнить функцию под замком и вернуть её результат.</summary>
	[KernMember] public R Run<R>(KFunc<R> f)
	{
		lock (_gate) return Kv.As<R>(f.Invoke());
	}
}

/// <summary>Захваченный замок: освобождается один раз, при `dispose()` (автоматически в `use`).</summary>
[KernType("lock_guard")]
public sealed class KernLockGuard(Action release)
{
	private Action? _release = release;

	[KernProperty] public bool IsHeld => _release is not null;

	[KernMember]
	public void Dispose()
	{
		var r = Interlocked.Exchange(ref _release, null);
		r?.Invoke();
	}
}

/// <summary>
/// Асинхронный замок: ждёт, не блокируя цикл событий, и не привязан к потоку — его можно держать через `await`.
/// <code>
/// amutex am = amutex()
/// async fun bump() {
///     use var g = await am.lock()
///     counter += 1
///     await delay(10)            // замок удерживается через await
/// }
/// </code>
/// </summary>
[KernType("amutex")]
public sealed class KernAsyncMutex
{
	private readonly SemaphoreSlim _sem = new(1, 1);

	[KernCtor] public KernAsyncMutex() { }

	[KernProperty] public bool IsLocked => _sem.CurrentCount == 0;

	/// <summary>Асинхронно захватить замок.</summary>
	[KernMember("lock")]
	public async Task<KernLockGuard> LockAsync()
	{
		await _sem.WaitAsync();
		return new KernLockGuard(() => _sem.Release());
	}

	/// <summary>Захватить без ожидания; nil — занято.</summary>
	[KernMember] public KernLockGuard? TryLock() =>
		_sem.Wait(0) ? new KernLockGuard(() => _sem.Release()) : null;
}

/// <summary>Замок «читатели — писатель»: много читателей одновременно или один писатель.</summary>
[KernType("rw_lock")]
public sealed class KernRwLock
{
	private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);

	[KernCtor] public KernRwLock() { }

	/// <summary>Захватить на чтение: `use var g = rw.read()`.</summary>
	[KernMember]
	public KernLockGuard Read()
	{
		_lock.EnterReadLock();
		return new KernLockGuard(() => _lock.ExitReadLock());
	}

	/// <summary>Захватить на запись (эксклюзивно): `use var g = rw.write()`.</summary>
	[KernMember]
	public KernLockGuard Write()
	{
		_lock.EnterWriteLock();
		return new KernLockGuard(() => _lock.ExitWriteLock());
	}
}

/// <summary>Атомарное 64-битное целое.</summary>
[KernType("atomic")]
public sealed class KernAtomic
{
	private long _value;

	[KernCtor] public KernAtomic() { }
	[KernCtor] public KernAtomic(long initial) => _value = initial;

	[KernProperty] public long Value { get => Interlocked.Read(ref _value); set => Interlocked.Exchange(ref _value, value); }
	/// <summary>Прибавить и вернуть новое значение.</summary>
	[KernMember] public long Add(long delta) => Interlocked.Add(ref _value, delta);
	[KernMember] public long Increment() => Interlocked.Increment(ref _value);
	[KernMember] public long Decrement() => Interlocked.Decrement(ref _value);
	[KernMember] public long Exchange(long v) => Interlocked.Exchange(ref _value, v);
	/// <summary>Записать desired, если текущее значение равно expected; возвращает прежнее значение.</summary>
	[KernMember] public long CompareExchange(long expected, long desired) => Interlocked.CompareExchange(ref _value, desired, expected);
}

/// <summary>Семафор: ограничивает число одновременных участников.</summary>
[KernType("semaphore")]
public sealed class KernSemaphore
{
	private readonly SemaphoreSlim _sem;

	[KernCtor] public KernSemaphore(int initial) => _sem = new(initial);

	[KernProperty] public int Available => _sem.CurrentCount;
	[KernMember] public void Wait() => _sem.Wait();
	[KernMember] public bool TryWait() => _sem.Wait(0);
	/// <summary>Асинхронное ожидание: `await s.wait_async()`.</summary>
	[KernMember] public Task WaitAsync() => _sem.WaitAsync();
	[KernMember] public void Release() => _sem.Release();
	[KernMember] public void Release(int count) => _sem.Release(count);
}
