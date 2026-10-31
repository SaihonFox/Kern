using Kern.Vm.Async;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>Корутины async-функций поверх фреймов в куче и однопоточного планировщика.</summary>
public sealed partial class Interpreter
{
	/// <summary>Запускает async-функцию: фрейм исполняется до первого незавершённого await.</summary>
	internal KTask StartCoroutine(Frame frame)
	{
		var task = new KTask(Scheduler);
		frame.Task = task;
		Step(frame, task, null);
		return task;
	}

	/// <summary>Продолжает корутину после завершения awaited-задачи <paramref name="done"/>.</summary>
	internal void ResumeCoroutine(KTask co, KTask done)
	{
		var frame = co.Parked;
		if (frame is null) return;
		co.Parked = null;
		if (done.IsFaulted) { co.Fail(done.Error ?? new KernPanic("задача упала")); return; }
		Step(frame, co, done.Result);
	}

	private void Step(Frame frame, KTask task, KValue? resume)
	{
		try
		{
			if (resume is { } rv) frame.Regs[frame.Sp++] = rv;
			var result = Execute(frame, out bool parked);
			if (!parked) task.Complete(result);
		}
		catch (KernExit) { throw; }
		catch (Exception e) { task.Fail(Interpreter.ToPanic(e)); }
	}
}
