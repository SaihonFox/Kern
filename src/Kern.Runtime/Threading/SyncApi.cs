using System.Collections.Concurrent;
using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Runtime;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Threading;

/// <summary>
/// Канал между потоками (spec round 24): значения передаются по ссылке, без копирования.
/// <c>send</c> блокирует поток, если ограниченный канал полон; <c>recv</c> ждёт значение,
/// после <c>close()</c> и опустошения возвращает <c>nil</c>.
/// </summary>
[KernType("channel", GenericArity = 1)]
public sealed class KernChannel
{
	private readonly BlockingCollection<KValue> _queue;

	public KernChannel() : this(0)
	{
	}

	/// <summary><paramref name="capacity"/> ≤ 0 — канал без ограничения.</summary>
	public KernChannel(int capacity) =>
		_queue = capacity > 0 ? new BlockingCollection<KValue>(capacity) : new BlockingCollection<KValue>();

	/// <summary>Текущее число значений в канале.</summary>
	public int Count => _queue.Count;

	/// <summary>Канал закрыт для новых <c>send</c>.</summary>
	public bool IsClosed => _queue.IsAddingCompleted;

	/// <summary>Отправить значение; <c>false</c> — канал закрыт.</summary>
	public bool Send(KValue value)
	{
		try
		{
			_queue.Add(value);
			return true;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}

	/// <summary>Принять значение (блокирует поток); <c>nil</c> — канал закрыт и пуст.</summary>
	public KValue Receive()
	{
		try
		{
			return _queue.Take();
		}
		catch (InvalidOperationException)
		{
			return KValue.Nil;
		}
	}

	/// <summary>Принять значение, не блокируясь; <c>nil</c> — сейчас пусто.</summary>
	public KValue TryReceive() => _queue.TryTake(out KValue value) ? value : KValue.Nil;

	/// <summary>Асинхронный приём: <c>await ch.recv_async()</c> не блокирует цикл событий.</summary>
	public Task<KValue> ReceiveAsync() => Task.Run(Receive);

	/// <summary>Закрыть канал: новые <c>send</c> вернут <c>false</c>, <c>recv</c> дочитает остаток.</summary>
	public void Close() => _queue.CompleteAdding();
}

/// <summary>Фабрика и члены <c>channel&lt;T&gt;</c>.</summary>
[KernType("channel", GenericArity = 1)]
public static class ChannelApi
{
	[KernCtor]
	[KernName("channel")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernChannel()));

	[KernCtor]
	[KernName("channel")]
	public static KValue Create(int capacity) => KValue.Obj(new KNativeBox(new KernChannel(capacity)));

	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	[KernName("is_closed")]
	[KernProperty]
	public static bool IsClosed(KNativeBox self) => Require(self).IsClosed;

	[KernMember]
	public static bool Send(KNativeBox self, KValue value) => Require(self).Send(value);

	[KernMember]
	[KernReturn("T?")]
	public static KValue Recv(KNativeBox self) => Require(self).Receive();

	[KernName("try_recv")]
	[KernMember]
	[KernReturn("T?")]
	public static KValue TryRecv(KNativeBox self) => Require(self).TryReceive();

	[KernName("recv_async")]
	[KernMember]
	[KernReturn("task<T?>")]
	public static Task<KValue> RecvAsync(KNativeBox self) => Require(self).ReceiveAsync();

	[KernMember]
	public static void Close(KNativeBox self) => Require(self).Close();

	private static KernChannel Require(KNativeBox self) => self.Value switch
	{
		KernChannel channel => channel,
		_ => throw new KernPanic($"channel: ожидался канал, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Захваченный замок: освобождается ровно один раз, из <c>use</c> — при выходе из блока.</summary>
[KernType("lock_guard")]
public sealed class KernLockGuard(Action release)
{
	private Action? _release = release;

	/// <summary>Замок ещё удерживается.</summary>
	public bool IsHeld => _release is not null;

	/// <summary>Освободить замок (повторный вызов ничего не делает).</summary>
	public void Dispose()
	{
		Action? release = Interlocked.Exchange(ref _release, null);
		release?.Invoke();
	}
}

/// <summary>Члены <c>lock_guard</c> (значение возвращают <c>guard()/read()/write()</c>).</summary>
[KernType("lock_guard")]
public static class LockGuardApi
{
	[KernName("is_held")]
	[KernProperty]
	public static bool IsHeld(KNativeBox self) => Require(self).IsHeld;

	[KernMember]
	public static void Dispose(KNativeBox self) => Require(self).Dispose();

	private static KernLockGuard Require(KNativeBox self) => self.Value switch
	{
		KernLockGuard guard => guard,
		_ => throw new KernPanic($"lock_guard: ожидался захваченный замок, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Реентерабельный <c>mutex</c> (spec round 24): основной путь — <c>use var g = m.guard()</c>.</summary>
[KernType("mutex")]
public sealed class KernMutex
{
	private readonly object _gate = new();

	/// <summary>Войти в критическую секцию (реентерабельно для того же потока).</summary>
	public void Enter() => Monitor.Enter(_gate);

	/// <summary>Попытаться войти без ожидания.</summary>
	public bool TryEnter() => Monitor.TryEnter(_gate);

	/// <summary>Выйти из критической секции.</summary>
	public void Exit() => Monitor.Exit(_gate);

	/// <summary>Захватить замок; освобождается через <c>dispose()</c> (автоматически в <c>use</c>).</summary>
	public KernLockGuard Guard()
	{
		Monitor.Enter(_gate);
		return new KernLockGuard(() => Monitor.Exit(_gate));
	}

	/// <summary>Захватить с ожиданием не дольше <paramref name="milliseconds"/>; <c>nil</c> — не удалось.</summary>
	public KernLockGuard? TryGuard(int milliseconds) =>
		Monitor.TryEnter(_gate, milliseconds) ? new KernLockGuard(() => Monitor.Exit(_gate)) : null;
}

/// <summary>Фабрика и члены <c>mutex</c>.</summary>
[KernType("mutex")]
public static class MutexApi
{
	[KernCtor]
	[KernName("mutex")]
	[KernReturn("mutex")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernMutex()));

	[KernMember]
	public static void Enter(KNativeBox self) => Require(self).Enter();

	[KernName("try_enter")]
	[KernMember]
	public static bool TryEnter(KNativeBox self) => Require(self).TryEnter();

	[KernMember]
	public static void Exit(KNativeBox self) => Require(self).Exit();

	[KernMember]
	[KernReturn("lock_guard")]
	public static KValue Guard(KNativeBox self) => KValue.Obj(new KNativeBox(Require(self).Guard()));

	[KernName("try_guard")]
	[KernMember]
	[KernReturn("lock_guard?")]
	public static KValue TryGuard(KNativeBox self, int milliseconds) =>
		Require(self).TryGuard(milliseconds) is { } guard
			? KValue.Obj(new KNativeBox(guard))
			: KValue.Nil;

	/// <summary><c>run(f)</c>: выполнить функцию под замком и вернуть результат.</summary>
	[KernMember]
	[KernReturn("T")]
	public static KValue Run<T>(Interpreter interpreter, KNativeBox self, KFunc<T> function) =>
		Require(self).Guard() is { } guard
			? RunGuarded(interpreter, function.Value, guard)
			: throw new KernPanic("mutex.run: не удалось захватить замок");

	private static KValue RunGuarded(Interpreter interpreter, KValue function, KernLockGuard guard)
	{
		try
		{
			return interpreter.RunValue(function);
		}
		finally
		{
			guard.Dispose();
		}
	}

	private static KernMutex Require(KNativeBox self) => self.Value switch
	{
		KernMutex mutex => mutex,
		_ => throw new KernPanic($"mutex: ожидался замок, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>
/// Асинхронный замок <c>amutex</c> (spec round 24): ждёт, не блокируя цикл событий, и не
/// привязан к потоку — его можно держать через <c>await</c>.
/// </summary>
[KernType("amutex")]
public sealed class KernAsyncMutex
{
	private readonly SemaphoreSlim _semaphore = new(1, 1);

	/// <summary>Замок сейчас удерживается.</summary>
	public bool IsLocked => _semaphore.CurrentCount == 0;

	/// <summary>Асинхронно захватить замок.</summary>
	public async Task<KValue> LockAsync()
	{
		await _semaphore.WaitAsync();
		return KValue.Obj(new KNativeBox(new KernLockGuard(() => _semaphore.Release())));
	}

	/// <summary>Захватить без ожидания; <c>nil</c> — занято.</summary>
	public KValue TryLock() =>
		_semaphore.Wait(0)
			? KValue.Obj(new KNativeBox(new KernLockGuard(() => _semaphore.Release())))
			: KValue.Nil;
}

/// <summary>Фабрика и члены <c>amutex</c>.</summary>
[KernType("amutex")]
public static class AsyncMutexApi
{
	[KernCtor]
	[KernName("amutex")]
	[KernReturn("amutex")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernAsyncMutex()));

	[KernName("is_locked")]
	[KernProperty]
	public static bool IsLocked(KNativeBox self) => Require(self).IsLocked;

	/// <summary><c>await am.lock()</c>: асинхронный захват.</summary>
	[KernMember]
	[KernName("lock")]
	[KernReturn("task<lock_guard>")]
	public static Task<KValue> LockAsync(KNativeBox self) => Require(self).LockAsync();

	[KernName("try_lock")]
	[KernMember]
	[KernReturn("lock_guard?")]
	public static KValue TryLock(KNativeBox self) => Require(self).TryLock();

	private static KernAsyncMutex Require(KNativeBox self) => self.Value switch
	{
		KernAsyncMutex mutex => mutex,
		_ => throw new KernPanic($"amutex: ожидался асинхронный замок, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Замок «читатели — писатель» (spec round 24).</summary>
[KernType("rw_lock")]
public sealed class KernRwLock
{
	private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);

	/// <summary>Захватить на чтение; освобождается через <c>dispose()</c>.</summary>
	public KernLockGuard Read()
	{
		_lock.EnterReadLock();
		return new KernLockGuard(() => _lock.ExitReadLock());
	}

	/// <summary>Захватить на запись (эксклюзивно).</summary>
	public KernLockGuard Write()
	{
		_lock.EnterWriteLock();
		return new KernLockGuard(() => _lock.ExitWriteLock());
	}
}

/// <summary>Фабрика и члены <c>rw_lock</c>.</summary>
[KernType("rw_lock")]
public static class RwLockApi
{
	[KernCtor]
	[KernName("rw_lock")]
	[KernReturn("rw_lock")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernRwLock()));

	[KernMember]
	[KernReturn("lock_guard")]
	public static KValue Read(KNativeBox self) => KValue.Obj(new KNativeBox(Require(self).Read()));

	[KernMember]
	[KernReturn("lock_guard")]
	public static KValue Write(KNativeBox self) => KValue.Obj(new KNativeBox(Require(self).Write()));

	private static KernRwLock Require(KNativeBox self) => self.Value switch
	{
		KernRwLock rw => rw,
		_ => throw new KernPanic($"rw_lock: ожидался замок чтения/записи, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Атомарное 64-битное целое (spec round 24).</summary>
[KernType("atomic")]
public sealed class KernAtomic
{
	private long _value;

	public KernAtomic()
	{
	}

	public KernAtomic(long initial) => _value = initial;

	/// <summary>Текущее значение.</summary>
	public long Value
	{
		get => Interlocked.Read(ref _value);
		set => Interlocked.Exchange(ref _value, value);
	}

	/// <summary>Прибавить и вернуть новое значение.</summary>
	public long Add(long delta) => Interlocked.Add(ref _value, delta);

	/// <summary>Увеличить на 1 и вернуть новое значение.</summary>
	public long Increment() => Interlocked.Increment(ref _value);

	/// <summary>Уменьшить на 1 и вернуть новое значение.</summary>
	public long Decrement() => Interlocked.Decrement(ref _value);

	/// <summary>Записать значение и вернуть прежнее.</summary>
	public long Exchange(long value) => Interlocked.Exchange(ref _value, value);

	/// <summary>Записать <paramref name="desired"/>, если текущее равно <paramref name="expected"/>.</summary>
	public long CompareExchange(long expected, long desired) =>
		Interlocked.CompareExchange(ref _value, desired, expected);
}

/// <summary>Фабрика и члены <c>atomic</c>.</summary>
[KernType("atomic")]
public static class AtomicApi
{
	[KernCtor]
	[KernName("atomic")]
	[KernReturn("atomic")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernAtomic()));

	[KernCtor]
	[KernName("atomic")]
	[KernReturn("atomic")]
	public static KValue Create(long initial) => KValue.Obj(new KNativeBox(new KernAtomic(initial)));

	[KernProperty]
	[KernName("value")]
	public static long GetValue(KNativeBox self) => Require(self).Value;

	[KernProperty]
	[KernName("value")]
	public static void SetValue(KNativeBox self, long value) => Require(self).Value = value;

	[KernMember]
	public static long Add(KNativeBox self, long delta) => Require(self).Add(delta);

	[KernMember]
	public static long Increment(KNativeBox self) => Require(self).Increment();

	[KernMember]
	public static long Decrement(KNativeBox self) => Require(self).Decrement();

	[KernMember]
	public static long Exchange(KNativeBox self, long value) => Require(self).Exchange(value);

	[KernName("compare_exchange")]
	[KernMember]
	public static long CompareExchange(KNativeBox self, long expected, long desired) =>
		Require(self).CompareExchange(expected, desired);

	private static KernAtomic Require(KNativeBox self) => self.Value switch
	{
		KernAtomic atomic => atomic,
		_ => throw new KernPanic($"atomic: ожидался атомарный счётчик, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Семафор (spec round 24).</summary>
[KernType("semaphore")]
public sealed class KernSemaphore(int initial)
{
	private readonly SemaphoreSlim _semaphore = new(initial);

	/// <summary>Сколько участников ещё может войти.</summary>
	public int Available => _semaphore.CurrentCount;

	/// <summary>Подождать (блокирует поток).</summary>
	public void Wait() => _semaphore.Wait();

	/// <summary>Попытаться войти без ожидания.</summary>
	public bool TryWait() => _semaphore.Wait(0);

	/// <summary>Асинхронно подождать (не блокирует цикл событий).</summary>
	public Task WaitAsync() => _semaphore.WaitAsync();

	/// <summary>Освободить одно место.</summary>
	public void Release() => _semaphore.Release();

	/// <summary>Освободить <paramref name="count"/> мест.</summary>
	public void Release(int count) => _semaphore.Release(count);
}

/// <summary>Фабрика и члены <c>semaphore</c>.</summary>
[KernType("semaphore")]
public static class SemaphoreApi
{
	[KernCtor]
	[KernName("semaphore")]
	[KernReturn("semaphore")]
	public static KValue Create(int initial) => KValue.Obj(new KNativeBox(new KernSemaphore(initial)));

	[KernProperty]
	public static int Available(KNativeBox self) => Require(self).Available;

	[KernMember]
	public static void Wait(KNativeBox self) => Require(self).Wait();

	[KernName("try_wait")]
	[KernMember]
	public static bool TryWait(KNativeBox self) => Require(self).TryWait();

	[KernName("wait_async")]
	[KernMember]
	[KernReturn("task<void>")]
	public static Task WaitAsync(KNativeBox self) => Require(self).WaitAsync();

	[KernMember]
	public static void Release(KNativeBox self) => Require(self).Release();

	[KernName("release")]
	[KernMember]
	public static void Release(KNativeBox self, int count) => Require(self).Release(count);

	private static KernSemaphore Require(KNativeBox self) => self.Value switch
	{
		KernSemaphore semaphore => semaphore,
		_ => throw new KernPanic($"semaphore: ожидался семафор, получено {self.Value.GetType().Name}"),
	};
}
