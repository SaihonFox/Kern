namespace Kern.Vm.Async;

/// <summary>
/// Одиночный цикл событий VM: все продолжения задач выполняются в потоке-владельце.
/// Внешние потоки (IO, сеть, <c>thread.run</c>) кладут работу через
/// <see cref="BeginExternal"/>/<see cref="EndExternal"/> или <see cref="Post"/>.
/// </summary>
public sealed class Scheduler
{
	private readonly Queue<Action> _queue = new();
	private readonly Lock _gate = new();
	private readonly AutoResetEvent _work = new(false);
	private readonly int _ownerThread;

	public Scheduler()
	{
		_ownerThread = Environment.CurrentManagedThreadId;
	}

	/// <summary>Поток, в котором разрешено выполнять продолжения.</summary>
	public int OwnerThread => _ownerThread;

	public bool IsOwnerThread => Environment.CurrentManagedThreadId == _ownerThread;

	public bool HasWork
	{
		get
		{
			lock (_gate) return _queue.Count > 0;
		}
	}

	/// <summary>Поставить работу в очередь. Безопасно из любого потока.</summary>
	public void Post(Action action)
	{
		ArgumentNullException.ThrowIfNull(action);
		lock (_gate)
			_queue.Enqueue(action);
		_work.Set();
	}

	/// <summary>Выполнить всю накопленную работу; новые задачи, поставленные во время выполнения, тоже.</summary>
	public void RunUntilIdle()
	{
		RequireOwnerThread();
		while (true)
		{
			Action action;
			lock (_gate)
			{
				if (_queue.Count == 0)
				{
					_work.Reset();
					return;
				}
				action = _queue.Dequeue();
			}
			action();
		}
	}

	/// <summary>Подождать появления работы (для синхронного <c>Run</c> верхнего уровня).</summary>
	public bool WaitForWork(TimeSpan timeout)
	{
		RequireOwnerThread();
		return _work.WaitOne(timeout);
	}

	/// <summary>Отметить начало работы внешнего потока (диагностика/контракт).</summary>
	public IDisposable BeginExternal()
	{
		return new ExternalScope();
	}

	/// <summary>Завершить внешнюю операцию: продолжение будет выполнено в потоке-владельце.</summary>
	public void EndExternal(Action completion)
	{
		ArgumentNullException.ThrowIfNull(completion);
		Post(completion);
	}

	private void RequireOwnerThread()
	{
		if (!IsOwnerThread)
			throw new InvalidOperationException(
				$"планировщик можно выполнять только в потоке-владельце {_ownerThread}, текущий {Environment.CurrentManagedThreadId}");
	}

	private sealed class ExternalScope : IDisposable
	{
		public void Dispose()
		{
		}
	}
}
