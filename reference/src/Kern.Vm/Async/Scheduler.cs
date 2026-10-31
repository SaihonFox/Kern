namespace Kern.Vm.Async;

/// <summary>
/// Однопоточный event loop VM. Весь Kern-код исполняется в потоке, который крутит RunUntil.
/// Внешние (.NET) операции регистрируются через BeginExternal/EndExternal, а их завершения безопасно
/// ставятся в очередь из любого потока методом Post.
/// </summary>
public sealed class Scheduler
{
	private readonly Queue<Action> _queue = new();
	private readonly object _lock = new();
	private int _pending;

	/// <summary>Число внешних операций, которые ещё не завершились.</summary>
	public int PendingExternal => Volatile.Read(ref _pending);

	/// <summary>Поставить действие в очередь (потокобезопасно).</summary>
	public void Post(Action action)
	{
		lock (_lock)
		{
			_queue.Enqueue(action);
			Monitor.Pulse(_lock);
		}
	}

	/// <summary>Отметить начало внешней операции (чтобы цикл не считал программу зависшей).</summary>
	public void BeginExternal() => Interlocked.Increment(ref _pending);

	/// <summary>Завершить внешнюю операцию: completion выполнится в потоке планировщика.</summary>
	public void EndExternal(Action completion) => Post(() =>
	{
		try { completion(); }
		finally { Interlocked.Decrement(ref _pending); }
	});

	/// <summary>Выполнить всё, что уже стоит в очереди, не блокируясь. Для встраивания в чужой UI-цикл.</summary>
	public int RunPending()
	{
		int n = 0;
		while (true)
		{
			Action? a;
			lock (_lock) { if (!_queue.TryDequeue(out a)) return n; }
			a();
			n++;
		}
	}

	/// <summary>
	/// Крутить очередь, пока не станет true условие done. Возвращает false, если делать больше нечего
	/// (очередь пуста, внешних операций нет), а условие не выполнено — «взаимная блокировка».
	/// </summary>
	public bool RunUntil(Func<bool> done)
	{
		while (true)
		{
			if (done()) return true;
			Action a;
			lock (_lock)
			{
				while (_queue.Count == 0)
				{
					if (done()) return true;
					if (Volatile.Read(ref _pending) == 0) return false;
					Monitor.Wait(_lock, 50);
				}
				a = _queue.Dequeue();
			}
			a();
		}
	}
}
