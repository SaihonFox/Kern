using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Threading;

/// <summary>Исполнение функции Kern в VM рабочего потока.</summary>
internal static class VmExec
{
	/// <summary>Размер стека рабочих потоков.</summary>
	public const int StackSize = 16 * 1024 * 1024;

	/// <summary>
	/// Вызвать функцию (или замыкание); async-функция ожидается циклом событий этой VM,
	/// после чего досрабатывают отложенные задачи рабочего.
	/// </summary>
	public static KValue Run(Interpreter wvm, KValue fn, KValue[] args)
	{
		var r = wvm.CallFunction(fn, args);
		if (r.Ref is Kern.Vm.Async.KTask t)
		{
			if (!t.IsCompleted && !wvm.Scheduler.RunUntil(() => t.IsCompleted))
				throw new KernPanic("взаимная блокировка в потоке");
			r = t.Unwrap();
		}
		wvm.Scheduler.RunUntil(() => wvm.Scheduler.PendingExternal == 0 && wvm.Scheduler.RunPending() == 0);
		return r;
	}

	/// <summary>Сообщение об ошибке для паники, выхода и прочих исключений рабочего.</summary>
	public static string Describe(Exception e) => e switch
	{
		KernPanic p => p.Message,
		KernExit x => $"поток вызвал exit({x.Code})",
		_ => e.Message,
	};
}
