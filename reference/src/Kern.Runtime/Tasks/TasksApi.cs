using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Tasks;

/// <summary>
/// Комбинаторы задач (`use kern.tasks[tasks]`):
/// <code>
/// var xs = await tasks.all([fetch(1), fetch(2)])          // дождаться всех
/// var first = await tasks.any([a(), b()])                 // первый УСПЕШНЫЙ
/// var fast = await tasks.race([a(), b()])                 // первый завершившийся (даже с ошибкой)
/// match await tasks.timeout(slow(), 500) { Ok(v) => …  Err(e) => println(e) }
/// var i = await tasks.select([ch1.recv_async(), ch2.recv_async()])   // индекс первой готовой
/// </code>
/// Все задачи живут в одном цикле событий VM: это кооперативная конкурентность; параллелизм на ядрах — `parallel` и `pool`.
/// </summary>
[KernType("tasks")]
public static class TasksApi
{
	private static List<KTask> Of(IEnumerable<KValue> items) => [.. items.Select(v => v.Ref as KTask ?? KTask.FromResult(v))];

	private static KValue Ok(KValue v) => KValue.Obj(new KResultObject(true, v));
	private static KValue Err(string message) => KValue.Obj(new KResultObject(false, KValue.Str(message)));

	/// <summary>Дождаться всех; при первой ошибке результат — эта ошибка. Результат — список в порядке задач.</summary>
	[KernStatic]
	public static KTaskRef<KList<T>> All<T>(KList<KTaskRef<T>> tasks, [KernTypeArgs] KType[] typeArgs) =>
		new(KTask.All(Of(tasks.Items), Kv.Last(typeArgs)));

	/// <summary>Первая успешная задача; если упали все — ошибка последней.</summary>
	[KernStatic]
	public static KTaskRef<T> Any<T>(KList<KTaskRef<T>> tasks)
	{
		var list = Of(tasks.Items);
		var kt = new KTask();
		if (list.Count == 0)
		{
			kt.Fail(new KernPanic("tasks.any: пустой список задач"));
			return new KTaskRef<T>(kt);
		}
		int failed = 0;
		foreach (var t in list)
			t.OnCompleted(_ =>
			{
				if (kt.IsCompleted) return;
				if (!t.IsFaulted) kt.Complete(t.Result);
				else if (++failed == list.Count) kt.Fail(t.Error!);
			});
		return new KTaskRef<T>(kt);
	}

	/// <summary>Первая завершившаяся задача (успех или ошибка).</summary>
	[KernStatic] public static KTaskRef<T> Race<T>(KList<KTaskRef<T>> tasks) => new(KTask.Any(Of(tasks.Items)));

	/// <summary>Дождаться всех без исключения: список `result` — Ok(значение) или Err(сообщение).</summary>
	[KernStatic]
	public static KTaskRef<KList<KResult<T, string>>> Settled<T>(KList<KTaskRef<T>> tasks)
	{
		var list = Of(tasks.Items);
		var kt = new KTask();
		int left = list.Count;
		if (left == 0)
		{
			kt.Complete(KValue.Obj(new KListObject(TypeFactory.Object)));
			return new KTaskRef<KList<KResult<T, string>>>(kt);
		}
		foreach (var t in list)
			t.OnCompleted(_ =>
			{
				if (--left > 0) return;
				var items = list.Select(x => x.IsFaulted ? Err(x.Error!.Message) : Ok(x.Result)).ToList();
				kt.Complete(KValue.Obj(new KListObject(TypeFactory.Object, items)));
			});
		return new KTaskRef<KList<KResult<T, string>>>(kt);
	}

	/// <summary>Индекс первой завершившейся задачи (для «select» по нескольким источникам).</summary>
	[KernStatic]
	public static KTaskRef<int> Select<T>(KList<KTaskRef<T>> tasks)
	{
		var list = Of(tasks.Items);
		var kt = new KTask();
		for (int i = 0; i < list.Count; i++)
		{
			int index = i;
			list[i].OnCompleted(_ => { if (!kt.IsCompleted) kt.Complete(KValue.I32(index)); });
		}
		return new KTaskRef<int>(kt);
	}

	/// <summary>Ждёт задачу не дольше `ms`: Ok(значение) либо Err("таймаут") / Err(ошибка задачи).</summary>
	[KernStatic]
	public static KTaskRef<KResult<T, string>> Timeout<T>(KTaskRef<T> task, int ms, [KernVm] Interpreter vm) =>
		new(TimeoutCore(task.Inner, ms, null, vm));

	/// <summary>То же, а по истечении времени дополнительно отменяет токен — так можно остановить саму работу.</summary>
	[KernStatic]
	public static KTaskRef<KResult<T, string>> Timeout<T>(KTaskRef<T> task, int ms, KernCancelToken token, [KernVm] Interpreter vm) =>
		new(TimeoutCore(task.Inner, ms, token, vm));

	private static KTask TimeoutCore(KTask inner, int ms, KernCancelToken? token, Interpreter vm)
	{
		var sched = vm.Scheduler;
		var kt = new KTask(sched);
		int ended = 0;
		Timer? timer = null;
		// Таймер считается внешней операцией, чтобы цикл событий не решил, что программа зависла. Выигрывает первый из двух путей.
		void End(Action completion)
		{
			if (Interlocked.Exchange(ref ended, 1) == 0) sched.EndExternal(completion);
		}
		sched.BeginExternal();
		timer = new Timer(_ => End(() =>
		{
			timer?.Dispose();
			if (kt.IsCompleted) return;
			token?.Cancel();
			kt.Complete(Err("таймаут"));
		}), null, System.Math.Max(0, ms), System.Threading.Timeout.Infinite);
		inner.OnCompleted(i =>
		{
			timer?.Dispose();
			End(() => { });
			if (kt.IsCompleted) return;
			kt.Complete(i.IsFaulted ? Err(i.Error!.Message) : Ok(i.Result));
		});
		return kt;
	}

	/// <summary>Пауза в миллисекундах (то же, что глобальная `delay`).</summary>
	[KernStatic] public static Task Delay(int ms) => Task.Delay(System.Math.Max(0, ms));

	/// <summary>Пауза, прерываемая токеном: при отмене задача завершается как отменённая.</summary>
	[KernStatic] public static Task Delay(int ms, KernCancelToken token) => Task.Delay(System.Math.Max(0, ms), token.Source.Token);

	/// <summary>Отдать управление циклу событий (другие задачи получают шанс выполниться).</summary>
	[KernStatic] public static Task YieldNow() => Task.CompletedTask;

	/// <summary>Запустить функцию как задачу в цикле событий (кооперативно, в том же потоке).</summary>
	[KernStatic] public static KTaskRef<R> Run<R>(KFunc<R> f) => new(KTask.Run(f.Inner.Vm, f.Inner.Function));

	/// <summary>Готовая задача со значением.</summary>
	[KernStatic] public static KTaskRef<T> FromResult<T>(T value) => new(KTask.FromResult(Kv.Of(value)));

	/// <summary>Задача, которая падает с сообщением (для тестов и заглушек).</summary>
	[KernStatic] public static KTaskRef<T> FromError<T>(string message) => new(KTask.FromError(new KernPanic(message)));

	/// <summary>Задача отменяется, как только отменён токен (если сама ещё не завершилась).</summary>
	[KernStatic]
	public static KTaskRef<T> WithCancel<T>(KTaskRef<T> task, KernCancelToken token, [KernVm] Interpreter vm)
	{
		var kt = new KTask(vm.Scheduler);
		task.Inner.OnCompleted(i =>
		{
			if (kt.IsCompleted) return;
			if (i.IsFaulted) kt.Fail(i.Error!); else kt.Complete(i.Result);
		});
		token.Source.Register(() => vm.Scheduler.Post(() => { if (!kt.IsCompleted) kt.Cancel(); }));
		return new KTaskRef<T>(kt);
	}

	/// <summary>Повторить асинхронную функцию до успеха: не более `attempts` попыток, между ними пауза `delay_ms`.</summary>
	[KernStatic]
	public static KTaskRef<T> Retry<T>(KFunc<KTaskRef<T>> f, int attempts, int delayMs)
	{
		var vm = f.Inner.Vm;
		var kt = new KTask(vm.Scheduler);
		int attempt = 0;

		void Failed(Exception e)
		{
			if (++attempt >= System.Math.Max(1, attempts)) { kt.Fail(e); return; }
			if (delayMs <= 0) vm.Scheduler.Post(Try);
			else KTask.Delay(delayMs).OnCompleted(_ => Try());
		}

		void Try()
		{
			KValue r;
			try { r = f.Inner.Invoke([]); }
			catch (KernPanic p) { Failed(p); return; }
			var t = r.Ref as KTask ?? KTask.FromResult(r);
			t.OnCompleted(i => { if (i.IsFaulted) Failed(i.Error!); else kt.Complete(i.Result); });
		}

		Try();
		return new KTaskRef<T>(kt);
	}
}
