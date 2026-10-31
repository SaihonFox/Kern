using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Runtime.Tasks;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Threading;

/// <summary>
/// Потоки с общей памятью. Все потоки видят одни и те же объекты, статические поля и замыкания:
/// то, что поток записал в общий объект, видят остальные. Синхронизация — за программой:
/// <c>mutex</c> (+ <c>use var g = m.guard()</c>), <c>rw_lock</c>, <c>atomic</c>, <c>shared&lt;T&gt;</c>, <c>channel&lt;T&gt;</c>,
/// потокобезопасные коллекции <c>conc_list</c>, <c>conc_dict</c>, <c>conc_queue</c>.
/// Обычные <c>list</c>/<c>dict</c> не потокобезопасны: из нескольких потоков их нужно защищать замком.
/// <code>
/// i32 total = 0
/// mutex m = mutex()
/// list&lt;worker&lt;i32&gt;&gt; ws = []
/// loop var i in 1..4 {
///     ws.add(thread.spawn(fun {
///         use var g = m.guard()
///         total += i
///         0
///     }))
/// }
/// loop var w in ws { w.join() }
/// </code>
/// </summary>
[KernType("thread")]
public static class ThreadApi
{
	[KernStatic("spawn")] public static KernWorker<R> Spawn<R>(KFunc<R> f, [KernVm] Interpreter vm) =>
		KernWorker<R>.Start(vm, f.Inner.Function, []);

	[KernStatic("spawn_with")] public static KernWorker<R> SpawnWith<A, R>(KFunc<A, R> f, A arg, [KernVm] Interpreter vm) =>
		KernWorker<R>.Start(vm, f.Inner.Function, [Kv.Of(arg)]);

	[KernStatic("spawn_with")] public static KernWorker<R> SpawnWith<A, B, R>(KFunc<A, B, R> f, A a, B b, [KernVm] Interpreter vm) =>
		KernWorker<R>.Start(vm, f.Inner.Function, [Kv.Of(a), Kv.Of(b)]);

	/// <summary>
	/// Выполнить функцию в общем пуле потоков и получить task: `var r = await thread.run(heavy)`.
	/// Цикл событий вызывающего не блокируется; функция видит общую память.
	/// </summary>
	[KernStatic("run")] public static KTaskRef<R> Run<R>(KFunc<R> f, [KernVm] Interpreter vm) =>
		new(KernPool.Shared(vm).Enqueue(vm, f.Inner.Function, []));

	[KernStatic("run_with")] public static KTaskRef<R> RunWith<A, R>(KFunc<A, R> f, A arg, [KernVm] Interpreter vm) =>
		new(KernPool.Shared(vm).Enqueue(vm, f.Inner.Function, [Kv.Of(arg)]));

	[KernStatic("run_with")] public static KTaskRef<R> RunWith<A, B, R>(KFunc<A, B, R> f, A a, B b, [KernVm] Interpreter vm) =>
		new(KernPool.Shared(vm).Enqueue(vm, f.Inner.Function, [Kv.Of(a), Kv.Of(b)]));

	/// <summary>Блокирующая пауза текущего потока (в async-коде используйте task.delay).</summary>
	[KernStatic] public static void Sleep(int ms) => System.Threading.Thread.Sleep(ms);

	[KernStatic] public static void Yield() => System.Threading.Thread.Yield();

	[KernStatic] public static int CpuCount() => Environment.ProcessorCount;

	/// <summary>Идентификатор текущего потока (для логов и отладки).</summary>
	[KernStatic] public static int CurrentId() => Environment.CurrentManagedThreadId;
}

/// <summary>Запущенный поток с результатом типа R.</summary>
[KernType("worker")]
public sealed class KernWorker<R>
{
	private readonly System.Threading.Thread _thread;
	private readonly TaskCompletionSource<KValue> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private KValue _result;
	private string? _error;

	private KernWorker(Interpreter parent, KValue function, KValue[] args)
	{
		var wvm = parent.Fork();
		_thread = new System.Threading.Thread(() => Run(wvm, function, args), VmExec.StackSize) { IsBackground = true };
	}

	internal static KernWorker<R> Start(Interpreter parent, KValue function, KValue[] args)
	{
		var w = new KernWorker<R>(parent, function, args);
		w._thread.Start();
		return w;
	}

	private void Run(Interpreter wvm, KValue fn, KValue[] args)
	{
		try { _result = VmExec.Run(wvm, fn, args); }
		catch (Exception e) { _error = VmExec.Describe(e); }
		finally { _done.TrySetResult(_result); }
	}

	private KResult<R, string> Outcome() =>
		_error is null ? KResult<R, string>.Ok(_result) : KResult<R, string>.Err(KValue.Str(_error));

	[KernProperty] public bool IsDone => _done.Task.IsCompleted;

	/// <summary>Блокирующее ожидание завершения: Ok(результат) или Err(сообщение паники).</summary>
	[KernMember] public KResult<R, string> Join()
	{
		_thread.Join();
		_done.Task.Wait();
		return Outcome();
	}

	/// <summary>Асинхронное ожидание (`await w.wait()`).</summary>
	[KernMember] public async Task<KResult<R, string>> Wait()
	{
		await _done.Task;
		return Outcome();
	}
}
