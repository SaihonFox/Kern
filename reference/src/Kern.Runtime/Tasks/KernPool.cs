using System.Collections.Concurrent;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Runtime.Threading;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Tasks;

/// <summary>
/// Пул рабочих потоков с общей памятью: долгоживущие рабочие VM разделяют объекты и статические поля программы.
/// Задачи отправляются `submit` и возвращают `task`; результат приходит в потоке VM вызывающего.
/// <code>
/// var p = pool(4)
/// atomic hits = atomic(0)
/// var r = await p.submit(fun { hits.increment(); ret 42 })
/// p.close()
/// </code>
/// Функция может быть замыканием: она видит захваченные переменные и объекты напрямую
/// (изменения общих обычных коллекций защищайте mutex'ом).
/// </summary>
[KernType("pool")]
public sealed class KernPool : IDisposable
{
	private sealed class Worker(Interpreter vm)
	{
		public Interpreter Vm { get; } = vm;
		public BlockingCollection<Action> Inbox { get; } = [];
		public int Pending;
	}

	private static KernPool? _shared;
	private static readonly object SharedGate = new();

	/// <summary>Общий пул по числу ядер — для `thread.run`; создаётся при первом обращении.</summary>
	internal static KernPool Shared(Interpreter vm)
	{
		lock (SharedGate) return _shared ??= new KernPool(0, vm);
	}

	private readonly List<Worker> _workers = [];
	private volatile bool _closed;

	[KernCtor] public KernPool([KernVm] Interpreter vm) : this(0, vm) { }

	[KernCtor]
	public KernPool(int workers, [KernVm] Interpreter vm)
	{
		int count = workers <= 0 ? Environment.ProcessorCount : workers;
		for (int i = 0; i < count; i++)
		{
			var worker = new Worker(vm.Fork());
			_workers.Add(worker);
			new System.Threading.Thread(() => Loop(worker), VmExec.StackSize) { IsBackground = true }.Start();
		}
	}

	[KernProperty] public int Workers => _workers.Count;
	[KernProperty] public bool IsClosed => _closed;

	/// <summary>Число задач, ещё не завершённых во всех рабочих.</summary>
	[KernProperty] public int Pending => _workers.Sum(w => Volatile.Read(ref w.Pending));

	/// <summary>Выполнить функцию без аргументов в пуле.</summary>
	[KernMember] public KTaskRef<R> Submit<R>(KFunc<R> f, [KernVm] Interpreter vm) => new(Enqueue(vm, f.Inner.Function, []));

	/// <summary>Выполнить функцию с аргументом.</summary>
	[KernMember] public KTaskRef<R> Submit<A, R>(KFunc<A, R> f, A arg, [KernVm] Interpreter vm) => new(Enqueue(vm, f.Inner.Function, [Kv.Of(arg)]));

	[KernMember] public KTaskRef<R> Submit<A, B, R>(KFunc<A, B, R> f, A a, B b, [KernVm] Interpreter vm) => new(Enqueue(vm, f.Inner.Function, [Kv.Of(a), Kv.Of(b)]));

	/// <summary>Больше не принимать задачи; уже принятые будут выполнены.</summary>
	[KernMember]
	public void Close()
	{
		if (_closed) return;
		_closed = true;
		foreach (var w in _workers) w.Inbox.CompleteAdding();
	}

	[KernMember] public void Dispose() => Close();

	/// <summary>Поставить задачу в очередь наименее загруженного рабочего; task принадлежит планировщику вызывающей VM.</summary>
	internal KTask Enqueue(Interpreter caller, KValue function, KValue[] args)
	{
		if (_closed) return KTask.FromError(new KernPanic("пул закрыт"));
		var sched = caller.Scheduler;
		var worker = _workers.MinBy(w => Volatile.Read(ref w.Pending))!;
		var kt = new KTask(sched);
		Interlocked.Increment(ref worker.Pending);
		sched.BeginExternal();
		try
		{
			worker.Inbox.Add(() =>
			{
				KValue result = default;
				string? error = null;
				try { result = VmExec.Run(worker.Vm, function, args); }
				catch (Exception e) { error = VmExec.Describe(e); }
				Interlocked.Decrement(ref worker.Pending);
				// завершаем task в потоке его VM — KTask не потокобезопасен
				sched.EndExternal(() =>
				{
					if (error is not null) kt.Fail(new KernPanic(error));
					else kt.Complete(result);
				});
			});
		}
		catch (InvalidOperationException)
		{
			// пул закрыли между проверкой и добавлением
			Interlocked.Decrement(ref worker.Pending);
			sched.EndExternal(() => kt.Fail(new KernPanic("пул закрыт")));
		}
		return kt;
	}

	private static void Loop(Worker worker)
	{
		foreach (var job in worker.Inbox.GetConsumingEnumerable()) job();
	}
}
