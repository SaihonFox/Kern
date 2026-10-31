using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Runtime.Threading;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Tasks;

/// <summary>
/// Параллельные циклы на ядрах процессора (`use kern.tasks[parallel]`). Рабочие потоки делят память с программой:
/// функция может быть замыканием и менять общее состояние (под mutex / atomic / conc_*).
/// Элементы раздаются динамически — быстрые рабочие берут больше.
/// <code>
/// var xs = await parallel.map([1, 2, 3, 4], fun(i32 x) -> i32 x * x)   // порядок результатов сохраняется
/// atomic hits = atomic(0)
/// await parallel.each(files, fun(str f) { process(f); hits.increment() })
/// await parallel.for_range(0, 1000, fun(i32 i) { cells.set(i, i * i) })   // cells — conc_list
/// var sum = await parallel.reduce(nums, 0, fun(i32 a, i32 b) -> i32 a + b)    // f ассоциативна, seed — нейтральный элемент
/// </code>
/// </summary>
[KernType("parallel")]
public static class ParallelApi
{
	/// <summary>Применить функцию к каждому элементу параллельно; результат — список в исходном порядке.</summary>
	[KernStatic]
	public static KTaskRef<KList<R>> Map<A, R>(KList<A> items, KFunc<A, R> f, [KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs) =>
		new(Map(vm, [.. items.Items], f.Inner.Function, 0, Kv.Last(typeArgs)));

	[KernStatic]
	public static KTaskRef<KList<R>> Map<A, R>(KList<A> items, KFunc<A, R> f, int workers, [KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs) =>
		new(Map(vm, [.. items.Items], f.Inner.Function, workers, Kv.Last(typeArgs)));

	/// <summary>Выполнить функцию для каждого элемента параллельно (результаты не нужны).</summary>
	[KernStatic]
	public static Task Each<A>(KList<A> items, KAction<A> f, [KernVm] Interpreter vm) => ToDotnet(Each(vm, [.. items.Items], f.Inner.Function, 0));

	[KernStatic]
	public static Task Each<A>(KList<A> items, KAction<A> f, int workers, [KernVm] Interpreter vm) => ToDotnet(Each(vm, [.. items.Items], f.Inner.Function, workers));

	/// <summary>Параллельный цикл по диапазону [from, to): `fun(i32 i)` вызывается для каждого индекса.</summary>
	[KernStatic]
	public static Task ForRange(int from, int to, KAction<int> f, [KernVm] Interpreter vm) =>
		ToDotnet(Each(vm, Range(from, to), f.Inner.Function, 0));

	[KernStatic]
	public static Task ForRange(int from, int to, KAction<int> f, int workers, [KernVm] Interpreter vm) =>
		ToDotnet(Each(vm, Range(from, to), f.Inner.Function, workers));

	/// <summary>
	/// Свёртка: каждый рабочий сворачивает свою долю, начиная с seed, затем доли объединяются той же функцией.
	/// Функция должна быть ассоциативной и коммутативной, seed — её нейтральным элементом (0 для суммы, 1 для произведения).
	/// </summary>
	[KernStatic]
	public static KTaskRef<A> Reduce<A>(KList<A> items, A seed, KFunc<A, A, A> f, [KernVm] Interpreter vm) =>
		new(Reduce(vm, [.. items.Items], Kv.Of(seed), f.Inner.Function, 0));

	[KernStatic]
	public static KTaskRef<A> Reduce<A>(KList<A> items, A seed, KFunc<A, A, A> f, int workers, [KernVm] Interpreter vm) =>
		new(Reduce(vm, [.. items.Items], Kv.Of(seed), f.Inner.Function, workers));

	// ---------------------------------------------------------------- ядро

	private static KValue[] Range(int from, int to)
	{
		if (to <= from) return [];
		var r = new KValue[to - from];
		for (int i = 0; i < r.Length; i++) r[i] = Kv.Of(from + i);
		return r;
	}

	private static Task ToDotnet(KTask kt)
	{
		var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		kt.OnCompleted(i => { if (i.IsFaulted) tcs.TrySetException(i.Error!); else tcs.TrySetResult(); });
		return tcs.Task;
	}

	private static KTask Map(Interpreter parent, KValue[] items, KValue function, int workers, KType elem)
	{
		var results = new KValue[items.Length];
		return Drive(parent, items.Length, workers,
			(vm, i) => results[i] = VmExec.Run(vm, function, [items[i]]),
			() => KValue.Obj(new KListObject(elem, [.. results])));
	}

	private static KTask Each(Interpreter parent, KValue[] items, KValue function, int workers) =>
		Drive(parent, items.Length, workers, (vm, i) => VmExec.Run(vm, function, [items[i]]), () => KValue.Nil);

	private static KTask Reduce(Interpreter parent, KValue[] items, KValue seed, KValue function, int workers)
	{
		var partials = new List<KValue>();
		var gate = new object();
		var kt = Drive(parent, items.Length, workers, null, () => KValue.Nil, vm =>
		{
			// свёртка доли: каждый рабочий накапливает локально и сдаёт частичный результат в конце
			return (i, acc) => acc is null ? VmExec.Run(vm, function, [seed, items[i]]) : VmExec.Run(vm, function, [acc.Value, items[i]]);
		}, acc => { lock (gate) partials.Add(acc); });
		var outer = new KTask(parent.Scheduler);
		kt.OnCompleted(i =>
		{
			if (i.IsFaulted) { outer.Fail(i.Error!); return; }
			try
			{
				var acc = seed;
				// партиалы объединяются в потоке VM-владельца
				foreach (var p in partials) acc = parent.CallFunction(function, [acc, p]);
				outer.Complete(acc);
			}
			catch (KernPanic p) { outer.Fail(p); }
		});
		return outer;
	}

	/// <summary>
	/// Запустить n единиц работы на пуле потоков с разделяемым счётчиком; задача завершается в потоке VM-владельца.
	/// <paramref name="body"/> — простая работа над индексом; <paramref name="folder"/> — режим свёртки с накопителем.
	/// </summary>
	private static KTask Drive(Interpreter parent, int n, int workers, Action<Interpreter, int>? body, Func<KValue> result,
		Func<Interpreter, Func<int, KValue?, KValue>>? folder = null, Action<KValue>? sink = null)
	{
		var sched = parent.Scheduler;
		var kt = new KTask(sched);
		if (n == 0)
		{
			kt.Complete(result());
			return kt;
		}
		workers = System.Math.Clamp(workers <= 0 ? Environment.ProcessorCount : workers, 1, n);

		var vms = new Interpreter[workers];
		for (int w = 0; w < workers; w++) vms[w] = parent.Fork();

		int next = -1;
		int remaining = workers;
		string? error = null;
		sched.BeginExternal();

		for (int w = 0; w < workers; w++)
		{
			var wvm = vms[w];
			var thread = new System.Threading.Thread(() =>
			{
				try
				{
					var step = folder?.Invoke(wvm);
					KValue? acc = null;
					int i;
					while (Volatile.Read(ref error) is null && (i = Interlocked.Increment(ref next)) < n)
					{
						if (step is not null) acc = step(i, acc);
						else body!(wvm, i);
					}
					if (acc is not null) sink?.Invoke(acc.Value);
				}
				catch (Exception e) { Interlocked.CompareExchange(ref error, VmExec.Describe(e), null); }
				finally
				{
					if (Interlocked.Decrement(ref remaining) == 0)
						sched.EndExternal(() =>
						{
							if (error is not null) { kt.Fail(new KernPanic(error)); return; }
							kt.Complete(result());
						});
				}
			}, VmExec.StackSize) { IsBackground = true };
			thread.Start();
		}
		return kt;
	}
}
