using Kern.Bytecode;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

public sealed class AsyncTests
{
	private static int[] AwaitAndReturn() =>
	[
		(int)OpCode.LoadLocal, 0,
		(int)OpCode.Await,
		(int)OpCode.Ret,
	];

	[Fact]
	public void Await_completed_task_pushes_result()
	{
		KTask task = KTask.FromResult(KValue.I32(42));

		KValue result = TestVm.Run(AwaitAndReturn(), paramCount: 1, localCount: 1, KValue.Obj(task));

		Assert.Equal(KValue.I32(42), result);
	}

	[Fact]
	public void Await_pending_task_parks_and_resumes()
	{
		KTask task = new();
		using ManualResetEventSlim started = new(false);
		Task completion = Task.Run(async () =>
		{
			await Task.Delay(30);
			started.Set();
			Assert.True(task.TrySetResult(KValue.I32(7)));
		});

		KValue result = TestVm.Run(AwaitAndReturn(), paramCount: 1, localCount: 1, KValue.Obj(task));

		Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
		completion.GetAwaiter().GetResult();
		Assert.Equal(KValue.I32(7), result);
	}

	[Fact]
	public void Await_failed_task_throws_panic_with_message()
	{
		KTask task = KTask.FromError(new KernPanic("сломалось"));

		KernPanic panic = Assert.Throws<KernPanic>(
			() => TestVm.Run(AwaitAndReturn(), paramCount: 1, localCount: 1, KValue.Obj(task)));

		Assert.Contains("сломалось", panic.Message);
		Assert.NotEmpty(panic.Frames);
	}

	[Fact]
	public void Await_two_tasks_in_reverse_completion_order()
	{
		KTask first = new();
		KTask second = new();
		int[] code =
		[
			(int)OpCode.LoadLocal, 0,
			(int)OpCode.Await,
			(int)OpCode.LoadLocal, 1,
			(int)OpCode.Await,
			(int)OpCode.Add,
			(int)OpCode.Ret,
		];

		Task completion = Task.Run(async () =>
		{
			await Task.Delay(10);
			second.TrySetResult(KValue.I32(2));
			await Task.Delay(10);
			first.TrySetResult(KValue.I32(40));
		});

		KValue result = TestVm.Run(code, paramCount: 2, localCount: 2, KValue.Obj(first), KValue.Obj(second));

		completion.GetAwaiter().GetResult();
		Assert.Equal(KValue.I32(42), result);
	}

	[Fact]
	public void Await_inside_nested_call_resumes_caller_frame()
	{
		FunctionDef main = TestVm.Function("main",
		[
			(int)OpCode.LoadLocal, 0,
			(int)OpCode.Call, 1, 1,
			(int)OpCode.Ret,
		], paramCount: 1, localCount: 1);
		FunctionDef inner = TestVm.Function("inner", AwaitAndReturn(), paramCount: 1, localCount: 1);

		KTask task = new();
		Task completion = Task.Run(async () =>
		{
			await Task.Delay(20);
			task.TrySetResult(KValue.I32(5));
		});

		KValue result = new Interpreter(TestVm.Module(main, inner)).Run(0, [KValue.Obj(task)]);

		completion.GetAwaiter().GetResult();
		Assert.Equal(KValue.I32(5), result);
	}

	[Fact]
	public void RunValue_invokes_function_value()
	{
		FunctionDef target = TestVm.Function("target",
		[
			(int)OpCode.LoadLocal, 0,
			(int)OpCode.PushI32, 1,
			(int)OpCode.Add,
			(int)OpCode.Ret,
		], paramCount: 1, localCount: 1);
		KbcModule module = TestVm.Module(target);
		Interpreter interpreter = new(module);

		KValue result = interpreter.RunValue(KValue.Obj(target), KValue.I32(41));

		Assert.Equal(KValue.I32(42), result);
	}

	[Fact]
	public void RunValue_invokes_closure_with_captures()
	{
		FunctionDef target = TestVm.Function("lambda",
		[
			(int)OpCode.LoadCapture, 0,
			(int)OpCode.Ret,
		]);
		KbcModule module = TestVm.Module(target);
		KClosure closure = new(target, [KValue.Str("захват")]);

		KValue result = new Interpreter(module).RunValue(KValue.Obj(closure));

		Assert.Equal(KValue.Str("захват"), result);
	}

	[Fact]
	public void Scheduler_runs_external_continuations_on_owner_thread()
	{
		Scheduler scheduler = new();
		int owner = Environment.CurrentManagedThreadId;
		int observed = 0;

		Task.Run(() => scheduler.Post(() => observed = Environment.CurrentManagedThreadId)).GetAwaiter().GetResult();
		scheduler.RunUntilIdle();

		Assert.Equal(owner, observed);
	}

	[Fact]
	public void Await_canceled_task_throws()
	{
		KTask task = KTask.Canceled();

		KernPanic panic = Assert.Throws<KernPanic>(
			() => TestVm.Run(AwaitAndReturn(), paramCount: 1, localCount: 1, KValue.Obj(task)));

		Assert.Contains("отменена", panic.Message);
	}
}
