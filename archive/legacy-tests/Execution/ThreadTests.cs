using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

public sealed class ThreadTests
{
	private static KbcModule SharedStaticsModule()
	{
		KbcModule module = new() { Name = "threads" };
		module.FieldPool.Add(new FieldDef { Name = "shared", Type = TypeSig.I32, Slot = 0 });
		module.FunctionPool.Add(TestVm.Function("write",
		[
			(int)OpCode.LoadLocal, 0,
			(int)OpCode.StoreStatic, 0,
			(int)OpCode.Ret,
		], paramCount: 1, localCount: 1));
		module.FunctionPool.Add(TestVm.Function("read",
		[
			(int)OpCode.LoadStatic, 0,
			(int)OpCode.Ret,
		]));
		return module;
	}

	[Fact]
	public void Fork_shares_statics()
	{
		KbcModule module = SharedStaticsModule();
		Interpreter owner = new(module);
		Interpreter worker = owner.Fork();

		owner.Run("write", KValue.I32(17));
		KValue observed = worker.Run("read", []);

		Assert.Equal(KValue.I32(17), observed);
	}

	[Fact]
	public void Fork_shares_static_writes_back()
	{
		KbcModule module = SharedStaticsModule();
		Interpreter owner = new(module);
		Interpreter worker = owner.Fork();

		worker.Run("write", KValue.I32(23));
		KValue observed = owner.Run("read", []);

		Assert.Equal(KValue.I32(23), observed);
	}

	[Fact]
	public void Fork_has_independent_stacks_and_schedulers()
	{
		FunctionDef recursive = TestVm.Function("main",
		[
			(int)OpCode.LoadLocal, 0,
			(int)OpCode.PushI32, 1,
			(int)OpCode.Add,
			(int)OpCode.Ret,
		], paramCount: 1, localCount: 1);
		KbcModule module = TestVm.Module(recursive);
		Interpreter owner = new(module);
		Interpreter worker = owner.Fork();

		KValue first = owner.Run(0, [KValue.I32(1)]);
		KValue second = worker.Run(0, [KValue.I32(10)]);

		Assert.Equal(KValue.I32(2), first);
		Assert.Equal(KValue.I32(11), second);
		Assert.NotSame(owner.Scheduler, worker.Scheduler);
	}

	[Fact]
	public void Fork_keeps_same_type_factory()
	{
		KbcModule module = SharedStaticsModule();
		Interpreter owner = new(module);
		Interpreter worker = owner.Fork();

		Assert.Same(owner.Types, worker.Types);
	}
}
