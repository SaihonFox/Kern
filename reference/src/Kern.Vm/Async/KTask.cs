using System.Reflection;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Vm.Async;

/// <summary>
/// task&lt;T&gt;: результат будущего значения. Создаётся async-функцией (корутина), нативом (FromDotnet) или вручную.
/// Все изменения состояния — только из потока планировщика.
/// </summary>
public sealed class KTask
{
	private List<Action<KTask>>? _continuations;
	private readonly Scheduler? _scheduler;

	public KTask(Scheduler? scheduler = null) => _scheduler = scheduler ?? Interpreter.Current?.Scheduler;

	public bool IsCompleted { get; private set; }
	public bool IsFaulted { get; private set; }
	public bool IsCanceled { get; private set; }
	public KValue Result { get; private set; }
	public Exception? Error { get; private set; }

	/// <summary>Припаркованный фрейм корутины (async-функция, ожидающая другую задачу).</summary>
	internal Frame? Parked { get; set; }

	public Scheduler? Scheduler => _scheduler;

	public static KTask FromResult(KValue value)
	{
		var t = new KTask();
		t.Complete(value);
		return t;
	}

	public static KTask FromError(Exception error)
	{
		var t = new KTask();
		t.Fail(error);
		return t;
	}

	public void Complete(KValue value)
	{
		if (IsCompleted) return;
		Result = value;
		IsCompleted = true;
		Fire();
	}

	public void Fail(Exception error)
	{
		if (IsCompleted) return;
		Error = error;
		IsFaulted = true;
		IsCompleted = true;
		Fire();
	}

	public void Cancel()
	{
		if (IsCompleted) return;
		IsCanceled = true;
		IsFaulted = true;
		Error = new KernPanic("задача отменена");
		IsCompleted = true;
		Fire();
	}

	private void Fire()
	{
		var list = _continuations;
		_continuations = null;
		if (list is null) return;
		foreach (var c in list) Dispatch(c);
	}

	private void Dispatch(Action<KTask> c)
	{
		if (_scheduler is null) c(this);
		else _scheduler.Post(() => c(this));
	}

	/// <summary>Подписаться на завершение (колбэк вызывается через планировщик).</summary>
	public void OnCompleted(Action<KTask> callback)
	{
		if (IsCompleted) Dispatch(callback);
		else (_continuations ??= []).Add(callback);
	}

	/// <summary>Значение результата; для упавшей задачи — выбрасывает её ошибку.</summary>
	public KValue Unwrap()
	{
		if (!IsCompleted) throw new InvalidOperationException("задача не завершена");
		if (IsFaulted) throw (Error is KernPanic || Error is KernExit) ? Error : new KernPanic(Error?.Message ?? "задача упала", Error);
		return Result;
	}

	/// <summary>Блокирующее ожидание (`task.result`): крутит планировщик, пока задача не завершится.</summary>
	public KValue GetResult(Interpreter vm)
	{
		if (!IsCompleted && !vm.Scheduler.RunUntil(() => IsCompleted))
			throw new KernPanic("взаимная блокировка: задача никогда не завершится");
		return Unwrap();
	}

	// ---------- мосты с .NET ----------

	private static Scheduler CurrentScheduler() =>
		Interpreter.Current?.Scheduler ?? throw new KernPanic("нет активной VM для создания задачи из .NET Task");

	public static KTask FromDotnet(Task task, Func<object?, KValue>? convert = null)
	{
		var sched = CurrentScheduler();
		var kt = new KTask(sched);
		sched.BeginExternal();
		task.ContinueWith(t => sched.EndExternal(() =>
		{
			if (t.IsCanceled) { kt.Cancel(); return; }
			if (t.IsFaulted) { kt.Fail(Flatten(t.Exception!)); return; }
			try { kt.Complete(ResultOf(t, convert)); }
			catch (Exception e) { kt.Fail(e); }
		}), TaskScheduler.Default);
		return kt;
	}

	public static KTask FromDotnet<T>(Task<T> task, Func<T, KValue>? convert = null) =>
		FromDotnet((Task)task, convert is null ? null : o => convert((T)o!));

	public static KTask FromValueTask(ValueTask task) => FromDotnet(task.AsTask());
	public static KTask FromValueTask<T>(ValueTask<T> task, Func<T, KValue>? convert = null) => FromDotnet(task.AsTask(), convert);

	private static Exception Flatten(AggregateException e) => e.InnerExceptions.Count == 1 ? e.InnerExceptions[0] : e;

	private static KValue ResultOf(Task t, Func<object?, KValue>? convert)
	{
		var type = t.GetType();
		if (!type.IsGenericType) return KValue.Void;
		object? value = type.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)?.GetValue(t);
		if (value?.GetType().FullName == "System.Threading.Tasks.VoidTaskResult") return KValue.Void;
		return convert is not null ? convert(value) : Num.FromClr(value);
	}

	/// <summary>`task.delay(ms)`.</summary>
	public static KTask Delay(int milliseconds, KCancellation? cancel = null) =>
		FromDotnet(Task.Delay(milliseconds, cancel?.Token ?? CancellationToken.None));

	/// <summary>Выполнить функцию Kern в планировщике как отдельную задачу (`task.run`).</summary>
	public static KTask Run(Interpreter vm, KValue function)
	{
		var kt = new KTask(vm.Scheduler);
		vm.Scheduler.Post(() =>
		{
			try
			{
				var r = vm.CallFunction(function, []);
				if (r.Ref is KTask inner) inner.OnCompleted(i => { if (i.IsFaulted) kt.Fail(i.Error!); else kt.Complete(i.Result); });
				else kt.Complete(r);
			}
			catch (Exception e) when (e is not KernExit) { kt.Fail(e); }
		});
		return kt;
	}

	/// <summary>Завершится, когда завершатся все задачи; результат — список значений.</summary>
	public static KTask All(IReadOnlyList<KTask> tasks, Types.KType elementType)
	{
		var kt = new KTask();
		int left = tasks.Count;
		if (left == 0) { kt.Complete(KValue.Obj(new KListObject(elementType))); return kt; }
		foreach (var t in tasks)
			t.OnCompleted(_ =>
			{
				if (kt.IsCompleted) return;
				if (t.IsFaulted) { kt.Fail(t.Error!); return; }
				if (--left == 0) kt.Complete(KValue.Obj(new KListObject(elementType, tasks.Select(x => x.Result).ToList())));
			});
		return kt;
	}

	/// <summary>Завершится вместе с первой завершившейся задачей.</summary>
	public static KTask Any(IReadOnlyList<KTask> tasks)
	{
		var kt = new KTask();
		foreach (var t in tasks)
			t.OnCompleted(_ =>
			{
				if (kt.IsCompleted) return;
				if (t.IsFaulted) kt.Fail(t.Error!); else kt.Complete(t.Result);
			});
		return kt;
	}
}
