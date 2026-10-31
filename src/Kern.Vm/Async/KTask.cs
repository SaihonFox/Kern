using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Async;

/// <summary>Состояние асинхронной задачи.</summary>
public enum KTaskStatus : byte
{
	Pending = 0,
	Succeeded,
	Failed,
	Canceled,
}

/// <summary>
/// Задача <c>task&lt;T&gt;</c>: результат, ошибка (panic) или отмена. Завершение может
/// прийти с другого потока (IO/сеть), поэтому продолжения выполняются только через
/// <see cref="Scheduler"/> потока-владельца (<see cref="Scheduler.BeginExternal"/>).
/// </summary>
public sealed class KTask
{
	private readonly Lock _gate = new();
	private readonly List<Action<KTask>> _continuations = [];

	/// <summary>Reified-тип результата (null — неизвестен/void).</summary>
	public KType? ResultType { get; init; }

	public KTaskStatus Status { get; private set; } = KTaskStatus.Pending;

	/// <summary>Результат успешной задачи; до завершения — <see cref="KValue.None"/>.</summary>
	public KValue Result { get; private set; } = KValue.None;

	/// <summary>Ошибка неуспешной задачи (panic из корутины или натива).</summary>
	public KernPanic? Error { get; private set; }

	public bool IsCompleted => Status != KTaskStatus.Pending;

	public bool IsSucceeded => Status == KTaskStatus.Succeeded;

	/// <summary>Создать уже завершённую успешную задачу.</summary>
	public static KTask FromResult(KValue value, KType? resultType = null) =>
		new() { ResultType = resultType, Status = KTaskStatus.Succeeded, Result = value };

	/// <summary>Создать уже завершённую задачу с ошибкой.</summary>
	public static KTask FromError(KernPanic error, KType? resultType = null) =>
		new() { ResultType = resultType, Status = KTaskStatus.Failed, Error = error };

	/// <summary>Создать уже отменённую задачу.</summary>
	public static KTask Canceled(KType? resultType = null) =>
		new() { ResultType = resultType, Status = KTaskStatus.Canceled };

	public bool TrySetResult(KValue value)
	{
		if (!TryComplete(KTaskStatus.Succeeded, value, null)) return false;
		return true;
	}

	public bool TrySetError(KernPanic error)
	{
		ArgumentNullException.ThrowIfNull(error);
		return TryComplete(KTaskStatus.Failed, KValue.None, error);
	}

	public bool TrySetCanceled() => TryComplete(KTaskStatus.Canceled, KValue.None, null);

	/// <summary>
	/// Подписаться на завершение. Если задача уже завершена, продолжение ставится в очередь
	/// планировщика (никогда не выполняется на чужом потоке синхронно).
	/// </summary>
	public void OnCompleted(Action<KTask> continuation, Scheduler scheduler)
	{
		ArgumentNullException.ThrowIfNull(continuation);
		ArgumentNullException.ThrowIfNull(scheduler);
		Action<KTask> posted = task => scheduler.Post(() => continuation(task));
		lock (_gate)
		{
			if (Status == KTaskStatus.Pending)
			{
				_continuations.Add(posted);
				return;
			}
		}
		posted(this);
	}

	private bool TryComplete(KTaskStatus status, KValue result, KernPanic? error)
	{
		Action<KTask>[] continuations;
		lock (_gate)
		{
			if (Status != KTaskStatus.Pending) return false;
			Status = status;
			Result = result;
			Error = error;
			continuations = [.. _continuations];
			_continuations.Clear();
		}

		foreach (var continuation in continuations)
			continuation(this);
		return true;
	}

	public override string ToString() => $"task({Status})";
}
