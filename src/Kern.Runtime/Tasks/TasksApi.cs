using Kern.Binding.Attributes;
using Kern.Binding.Marshalling;
using Kern.Binding.Values;
using Kern.Bytecode;
using Kern.Runtime.Core;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Tasks;

/// <summary>
/// Комбинаторы задач <c>tasks.*</c> (spec round 23): <c>all/any/race/settled/select/
/// timeout/delay/run/from_result/from_error/with_cancel/retry/yield_now</c>. Все задачи
/// живут в одном цикле событий VM — это кооперативная конкурентность; параллелизм на
/// ядрах дают <c>parallel</c> и <c>pool</c>.
/// </summary>
[KernType("tasks")]
public static class TasksApi
{
	[KernStatic]
	[KernReturn("task<list<T>>")]
	public static KValue All<T>(Interpreter interpreter, KList<KTask<T>> tasks, [KernTypeArgs] KType[] typeArguments)
	{
		List<KTask> items = Unwrap(tasks);
		KType element = typeArguments.Length > 0 ? typeArguments[0] : interpreter.Types.Prim(PrimKind.Object);
		var result = new KTask();
		if (items.Count == 0)
		{
			result.Complete(KValue.Obj(new KList(element, [])));
			return KValue.Obj(result);
		}

		int remaining = items.Count;
		foreach (KTask item in items)
		{
			item.OnCompleted(completed =>
			{
				if (result.IsCompleted)
					return;
				if (completed.Status != KTaskStatus.Succeeded)
				{
					FailFrom(result, completed);
					return;
				}

				if (Interlocked.Decrement(ref remaining) == 0)
					result.Complete(KValue.Obj(new KList(element, [.. items.Select(task => task.Result)])));
			}, interpreter.Scheduler);
		}

		return KValue.Obj(result);
	}

	[KernStatic]
	[KernReturn("task<T>")]
	public static KValue Any<T>(Interpreter interpreter, KList<KTask<T>> tasks, [KernTypeArgs] KType[] typeArguments)
	{
		List<KTask> items = Unwrap(tasks);
		var result = new KTask();
		if (items.Count == 0)
		{
			result.Fail(new KernPanic("tasks.any: пустой список задач"));
			return KValue.Obj(result);
		}

		int failed = 0;
		foreach (KTask item in items)
		{
			item.OnCompleted(completed =>
			{
				if (result.IsCompleted)
					return;
				if (completed.Status == KTaskStatus.Succeeded)
				{
					result.Complete(completed.Result);
					return;
				}

				if (Interlocked.Increment(ref failed) == items.Count)
					FailFrom(result, completed);
			}, interpreter.Scheduler);
		}

		return KValue.Obj(result);
	}

	[KernStatic]
	[KernReturn("task<T>")]
	public static KValue Race<T>(Interpreter interpreter, KList<KTask<T>> tasks, [KernTypeArgs] KType[] typeArguments)
	{
		List<KTask> items = Unwrap(tasks);
		var result = new KTask();
		if (items.Count == 0)
		{
			result.Fail(new KernPanic("tasks.race: пустой список задач"));
			return KValue.Obj(result);
		}

		foreach (KTask item in items)
		{
			item.OnCompleted(completed =>
			{
				if (result.IsCompleted)
					return;
				if (completed.Status == KTaskStatus.Succeeded)
					result.Complete(completed.Result);
				else
					FailFrom(result, completed);
			}, interpreter.Scheduler);
		}

		return KValue.Obj(result);
	}

	[KernStatic]
	[KernReturn("task<list<result<T, str>>>")]
	public static KValue Settled<T>(Interpreter interpreter, KList<KTask<T>> tasks, [KernTypeArgs] KType[] typeArguments)
	{
		List<KTask> items = Unwrap(tasks);
		KType valueType = typeArguments.Length > 0 ? typeArguments[0] : interpreter.Types.Prim(PrimKind.Object);
		KType errorType = interpreter.Types.Prim(PrimKind.Str);
		var result = new KTask();
		int remaining = items.Count;
		if (remaining == 0)
		{
			result.Complete(KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), [])));
			return KValue.Obj(result);
		}

		foreach (KTask item in items)
		{
			item.OnCompleted(_ =>
			{
				if (interpreter.Scheduler.IsOwnerThread)
				{
					if (Interlocked.Decrement(ref remaining) == 0)
						result.Complete(BuildSettled(interpreter, items, valueType, errorType));
					return;
				}

				interpreter.Scheduler.Post(() =>
				{
					if (Interlocked.Decrement(ref remaining) == 0)
						result.Complete(BuildSettled(interpreter, items, valueType, errorType));
				});
			}, interpreter.Scheduler);
		}

		return KValue.Obj(result);
	}

	[KernStatic]
	[KernReturn("task<i32>")]
	public static KValue Select<T>(Interpreter interpreter, KList<KTask<T>> tasks, [KernTypeArgs] KType[] typeArguments)
	{
		List<KTask> items = Unwrap(tasks);
		var result = new KTask();
		if (items.Count == 0)
		{
			result.Fail(new KernPanic("tasks.select: пустой список задач"));
			return KValue.Obj(result);
		}

		for (var index = 0; index < items.Count; index++)
		{
			int position = index;
			items[index].OnCompleted(_ =>
			{
				if (!result.IsCompleted)
					result.Complete(KValue.I32(position));
			}, interpreter.Scheduler);
		}

		return KValue.Obj(result);
	}

	[KernStatic]
	[KernReturn("task<result<T, str>>")]
	public static KValue Timeout<T>(
		Interpreter interpreter,
		KTask<T> pending,
		int milliseconds,
		[KernTypeArgs] KType[] typeArguments)
	{
		KType valueType = typeArguments.Length > 0 ? typeArguments[0] : interpreter.Types.Prim(PrimKind.Object);
		KTask inner = pending.Task;
		var result = new KTask();
		int finished = 0;

		inner.OnCompleted(completed =>
		{
			if (Interlocked.Exchange(ref finished, 1) != 0)
				return;
			result.Complete(ResultOf(interpreter, completed, valueType));
		}, interpreter.Scheduler);

		_ = Task.Delay(System.Math.Max(0, milliseconds)).ContinueWith(_ =>
		{
			if (Interlocked.Exchange(ref finished, 1) != 0)
				return;
			interpreter.Scheduler.EndExternal(() =>
				result.Complete(KernResults.Err(
					interpreter,
					interpreter.Types.Prim(PrimKind.Str),
					KValue.Str("таймаут"))));
		}, TaskScheduler.Default);

		return KValue.Obj(result);
	}

	/// <summary><c>tasks.delay(ms)</c>: пауза, не блокирующая цикл событий.</summary>
	[KernStatic]
	public static Task Delay(int milliseconds) => Task.Delay(System.Math.Max(0, milliseconds));

	/// <summary><c>tasks.yield_now()</c>: отдать управление другим задачам.</summary>
	[KernStatic]
	[KernName("yield_now")]
	public static Task YieldNow() => Task.CompletedTask;

	/// <summary><c>tasks.run(f)</c>: запустить функцию корутиной в этом цикле событий.</summary>
	[KernStatic]
	[KernReturn("task<T>")]
	public static KValue Run<T>(Interpreter interpreter, KFunc<T> function) =>
		KValue.Obj(interpreter.StartAsync(function.Value));

	[KernStatic]
	[KernReturn("task<T>")]
	public static KValue FromResult<T>(T value) =>
		KValue.Obj(KTask.FromResult(KMarshal.FromObject(value)));

	[KernStatic]
	[KernReturn("task<T>")]
	public static KValue FromError<T>(string message) =>
		KValue.Obj(KTask.FromError(new KernPanic(message)));

	/// <summary><c>tasks.with_cancel(task, token)</c>: отменить задачу вместе с токеном.</summary>
	[KernStatic]
	[KernName("with_cancel")]
	[KernReturn("task<T>")]
	public static KValue WithCancel<T>(Interpreter interpreter, KTask<T> pending, KValue token)
	{
		KernCancelToken source = CancelTokenApi.RequireValue(token);
		KTask inner = pending.Task;
		var result = new KTask();
		inner.OnCompleted(completed =>
		{
			if (!result.IsCompleted)
				Forward(result, completed);
		}, interpreter.Scheduler);
		RegisterCancel(source, interpreter, () =>
		{
			if (!result.IsCompleted)
				result.Cancel();
		});
		return KValue.Obj(result);
	}

	/// <summary><c>tasks.retry(f, attempts, delay_ms)</c>: повторять асинхронную функцию до успеха.</summary>
	[KernStatic]
	[KernReturn("task<T>")]
	public static KValue Retry<T>(Interpreter interpreter, KFunc<KTask<T>> function, int attempts, int delayMilliseconds)
	{
		var result = new KTask();
		int remaining = System.Math.Max(1, attempts);

		void Next()
		{
			KValue value;
			try
			{
				value = interpreter.RunValue(function.Value);
			}
			catch (KernPanic panic)
			{
				RetryLater(panic);
				return;
			}

			KTask task = value.Ref as KTask ?? KTask.FromResult(value);
			task.OnCompleted(completed =>
			{
				if (completed.Status == KTaskStatus.Succeeded)
				{
					result.Complete(completed.Result);
					return;
				}

				RetryLater(completed.Error ?? new KernPanic("задача завершилась отказом"));
			}, interpreter.Scheduler);
		}

		void RetryLater(KernPanic error)
		{
			if (--remaining <= 0)
			{
				result.Fail(error);
				return;
			}

			_ = Task.Delay(System.Math.Max(0, delayMilliseconds)).ContinueWith(_ =>
				interpreter.Scheduler.EndExternal(Next), TaskScheduler.Default);
		}

		Next();
		return KValue.Obj(result);
	}

	internal static void Forward(KTask target, KTask completed)
	{
		if (completed.Status == KTaskStatus.Succeeded)
			target.Complete(completed.Result);
		else if (completed.Status == KTaskStatus.Canceled)
			target.Cancel();
		else
			target.Fail(completed.Error ?? new KernPanic("задача завершилась ошибкой"));
	}

	internal static void FailFrom(KTask target, KTask completed) => Forward(target, completed);

	internal static void RegisterCancel(KernCancelToken token, Interpreter interpreter, Action action)
	{
		if (token.IsCancelled)
		{
			interpreter.Scheduler.Post(action);
			return;
		}

		token.RegisterCancellation(() => interpreter.Scheduler.Post(action));
	}

	private static KValue BuildSettled(
		Interpreter interpreter,
		IReadOnlyList<KTask> items,
		KType valueType,
		KType errorType)
	{
		var entries = new List<KValue>(items.Count);
		foreach (KTask item in items)
		{
			if (item.Status == KTaskStatus.Succeeded)
				entries.Add(KernResults.Ok(interpreter, valueType, item.Result));
			else
				entries.Add(KernResults.Err(
					interpreter,
					errorType,
					KValue.Str(item.Error?.Message ?? "задача отменена")));
		}

		return KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), entries));
	}

	private static KValue ResultOf(Interpreter interpreter, KTask completed, KType valueType) =>
		completed.Status == KTaskStatus.Succeeded
			? KernResults.Ok(interpreter, valueType, completed.Result)
			: KernResults.Err(
				interpreter,
				interpreter.Types.Prim(PrimKind.Str),
				KValue.Str(completed.Error?.Message ?? "задача отменена"));

	private static List<KTask> Unwrap<T>(KList<KTask<T>> tasks) =>
		[.. tasks.Select(task => task.Task)];
}

/// <summary>Члены <c>task&lt;T&gt;</c> (spec round 23).</summary>
[KernType("task", GenericArity = 1)]
public static class TaskApi
{
	[KernProperty]
	public static bool Done(KValue self) => Require(self).IsCompleted;

	[KernProperty]
	public static bool Failed(KValue self) => Require(self).IsFaulted;

	[KernProperty]
	public static bool Cancelled(KValue self) => Require(self).IsCanceled;

	[KernProperty]
	[KernReturn("str?")]
	public static KValue Error(KValue self) =>
		Require(self).Error is { } error ? KValue.Str(error.Message) : KValue.Nil;

	[KernMember]
	public static void Cancel(KValue self) => Require(self).Cancel();

	[KernProperty]
	[KernReturn("T")]
	public static KValue Result(Interpreter interpreter, KValue self) => Require(self).GetResult(interpreter);

	private static KTask Require(KValue self) => self.Ref as KTask
		?? throw new KernPanic($"task: ожидалась задача, получено {self.TypeName}");
}
