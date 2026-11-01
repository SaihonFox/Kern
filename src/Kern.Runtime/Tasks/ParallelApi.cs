using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Bytecode;
using Kern.Runtime.Threading;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Tasks;

/// <summary>
/// Параллельные циклы на ядрах (spec round 24): <c>map</c>, <c>each</c>,
/// <c>for_range</c>, <c>reduce</c>. Замыкание видит общую память (как в C#),
/// каждый рабочий поток получает форк интерпретатора.
/// </summary>
[KernType("parallel")]
public static class ParallelApi
{
	[KernStatic]
	[KernReturn("task<list<R>>")]
	public static KValue Map<T, R>(
		Interpreter interpreter,
		KList<T> items,
		KFunc<T, R> function,
		[KernTypeArgs] KType[] typeArguments) =>
		Map(interpreter, items, function, Environment.ProcessorCount, typeArguments);

	[KernStatic]
	[KernReturn("task<list<R>>")]
	public static KValue Map<T, R>(
		Interpreter interpreter,
		KList<T> items,
		KFunc<T, R> function,
		int workers,
		[KernTypeArgs] KType[] typeArguments)
	{
		KValue[] source = items.Raw.Items.ToArray();
		KType resultElement = typeArguments.Length > 1 ? typeArguments[1] : interpreter.Types.Prim(PrimKind.Object);
		var results = new KValue[source.Length];
		return RunWorkers(interpreter, source.Length, workers, (workerVm, range) =>
		{
			foreach (int index in range)
				results[index] = function.Raw.Invoke(workerVm, source[index]);
		}, () => KValue.Obj(new KList(resultElement, results)));
	}

	[KernStatic]
	[KernReturn("task<void>")]
	public static KValue Each<T>(Interpreter interpreter, KList<T> items, KAction<T> action)
	{
		KValue[] source = items.Raw.Items.ToArray();
		return RunWorkers(interpreter, source.Length, Environment.ProcessorCount, (workerVm, range) =>
		{
			foreach (int index in range)
				action.Raw.Invoke(workerVm, source[index]);
		}, static () => KValue.Void);
	}

	[KernStatic]
	[KernName("for_range")]
	[KernReturn("task<void>")]
	public static KValue ForRange(Interpreter interpreter, int from, int to, KAction<int> action)
	{
		int count = System.Math.Max(0, to - from);
		return RunWorkers(interpreter, count, Environment.ProcessorCount, (workerVm, range) =>
		{
			foreach (int index in range)
				action.Raw.Invoke(workerVm, KValue.I32(from + index));
		}, static () => KValue.Void);
	}

	[KernStatic]
	[KernReturn("task<T>")]
	public static KValue Reduce<T>(
		Interpreter interpreter,
		KList<T> items,
		T seed,
		KFunc<T, T, T> function,
		[KernTypeArgs] KType[] typeArguments)
	{
		KValue[] source = items.Raw.Items.ToArray();
		KValue identity = Kern.Binding.Marshalling.KMarshal.FromObject(seed);
		object gate = new();
		var partials = new List<KValue>();
		return RunWorkers(interpreter, source.Length, Environment.ProcessorCount, (workerVm, range) =>
		{
			KValue local = identity;
			foreach (int index in range)
				local = function.Raw.Invoke(workerVm, local, source[index]);
			lock (gate)
				partials.Add(local);
		}, () =>
		{
			KValue total = identity;
			foreach (KValue partial in partials)
				total = function.Raw.Invoke(interpreter, total, partial);
			return total;
		});
	}

	/// <summary>
	/// Раздать индексы <paramref name="count"/> рабочим потокам и завершить задачу
	/// в потоке-владельце вызывающей VM.
	/// </summary>
	internal static KValue RunWorkers(
		Interpreter caller,
		int count,
		int workers,
		Action<Interpreter, IEnumerable<int>> body,
		Func<KValue> collect)
	{
		var result = new KTask();
		int threadCount = System.Math.Min(System.Math.Max(1, workers), System.Math.Max(1, count));
		if (count == 0)
		{
			result.Complete(collect());
			return KValue.Obj(result);
		}

		int next = -1;
		int remaining = threadCount;
		for (var workerIndex = 0; workerIndex < threadCount; workerIndex++)
		{
			var thread = new Thread(() =>
			{
				try
				{
					Interpreter workerVm = caller.Fork();
					var range = new List<int>();
					while (true)
					{
						int index = Interlocked.Increment(ref next);
						if (index >= count)
							break;
						range.Add(index);
					}

					body(workerVm, range);
				}
				catch (Exception error)
				{
					caller.Scheduler.EndExternal(() =>
						result.Fail(error as KernPanic ?? new KernPanic(VmExec.Describe(error), error)));
					return;
				}
				finally
				{
					if (Interlocked.Decrement(ref remaining) == 0 && !result.IsCompleted)
					{
						try
						{
							KValue value = collect();
							caller.Scheduler.EndExternal(() => result.Complete(value));
						}
						catch (Exception error)
						{
							caller.Scheduler.EndExternal(() =>
								result.Fail(error as KernPanic ?? new KernPanic(VmExec.Describe(error), error)));
						}
					}
				}
			}, VmExec.StackSize)
			{
				IsBackground = true,
			};
			thread.Start();
		}

		return KValue.Obj(result);
	}
}

/// <summary>
/// Пул рабочих потоков с общей памятью (spec round 24): задачи отправляются
/// <c>submit</c>, результат приходит в потоке VM вызывающего.
/// </summary>
[KernType("pool")]
public sealed class KernPool : IDisposable
{
	private readonly List<Worker> _workers = [];
	private bool _closed;

	private sealed class Worker(Interpreter parent)
	{
		public Interpreter Parent { get; } = parent;
		public Thread Thread { get; set; } = null!;
		public System.Collections.Concurrent.BlockingCollection<Action<Interpreter>> Inbox { get; } = [];
	}

	public KernPool(Interpreter parent, int workers)
	{
		int count = workers <= 0 ? Environment.ProcessorCount : workers;
		for (var index = 0; index < count; index++)
		{
			var worker = new Worker(parent);
			worker.Thread = new Thread(() => Loop(worker), VmExec.StackSize) { IsBackground = true };
			_workers.Add(worker);
			worker.Thread.Start();
		}
	}

	public int Workers => _workers.Count;

	public bool IsClosed
	{
		get
		{
			lock (_workers) return _closed;
		}
	}

	public int Pending => _workers.Sum(worker => worker.Inbox.Count);

	public KTask Submit(Interpreter caller, KValue function, KValue[] arguments)
	{
		lock (_workers)
		{
			if (_closed)
				return KTask.FromError(new KernPanic("пул закрыт"));
		}

		var result = new KTask();
		Worker worker = _workers.MinBy(item => item.Inbox.Count)!;
		worker.Inbox.Add(workerVm =>
		{
			try
			{
				KValue value = VmExec.Run(workerVm, function, arguments);
				caller.Scheduler.EndExternal(() => result.Complete(value));
			}
			catch (Exception error)
			{
				caller.Scheduler.EndExternal(() =>
					result.Fail(error as KernPanic ?? new KernPanic(VmExec.Describe(error), error)));
			}
		});
		return result;
	}

	public void Close()
	{
		lock (_workers)
		{
			if (_closed)
				return;
			_closed = true;
		}

		foreach (Worker worker in _workers)
			worker.Inbox.CompleteAdding();
	}

	public void Dispose() => Close();

	private static void Loop(Worker worker)
	{
		// Форк создаётся в рабочем потоке: планировщик принадлежит ему.
		Interpreter vm = worker.Parent.Fork();
		foreach (Action<Interpreter> job in worker.Inbox.GetConsumingEnumerable())
			job(vm);
	}
}

/// <summary>Фабрика и члены <c>pool</c>.</summary>
[KernType("pool")]
public static class PoolApi
{
	[KernCtor]
	[KernName("pool")]
	[KernReturn("pool")]
	public static KValue Create(Interpreter interpreter) =>
		KValue.Obj(new KNativeBox(new KernPool(interpreter, Environment.ProcessorCount)));

	[KernCtor]
	[KernName("pool")]
	[KernReturn("pool")]
	public static KValue Create(Interpreter interpreter, int workers) =>
		KValue.Obj(new KNativeBox(new KernPool(interpreter, workers)));

	[KernProperty]
	public static int Workers(KNativeBox self) => Require(self).Workers;

	[KernName("is_closed")]
	[KernProperty]
	public static bool IsClosed(KNativeBox self) => Require(self).IsClosed;

	[KernProperty]
	public static int Pending(KNativeBox self) => Require(self).Pending;

	[KernMember]
	public static void Close(KNativeBox self) => Require(self).Close();

	[KernMember]
	public static void Dispose(KNativeBox self) => Require(self).Dispose();

	[KernMember]
	[KernReturn("task<R>")]
	public static KValue Submit<R>(Interpreter interpreter, KNativeBox self, KFunc<R> function) =>
		KValue.Obj(Require(self).Submit(interpreter, function.Value, []));

	[KernMember]
	[KernReturn("task<R>")]
	public static KValue Submit<A, R>(
		Interpreter interpreter,
		KNativeBox self,
		KFunc<A, R> function,
		A argument) =>
		KValue.Obj(Require(self).Submit(
			interpreter, function.Value, [Kern.Binding.Marshalling.KMarshal.FromObject(argument)]));

	[KernMember]
	[KernReturn("task<R>")]
	public static KValue Submit<A, B, R>(
		Interpreter interpreter,
		KNativeBox self,
		KFunc<A, B, R> function,
		A first,
		B second) =>
		KValue.Obj(Require(self).Submit(
			interpreter,
			function.Value,
			[Kern.Binding.Marshalling.KMarshal.FromObject(first), Kern.Binding.Marshalling.KMarshal.FromObject(second)]));

	private static KernPool Require(KNativeBox self) => self.Value switch
	{
		KernPool pool => pool,
		_ => throw new KernPanic($"pool: ожидался пул потоков, получено {self.Value.GetType().Name}"),
	};
}
