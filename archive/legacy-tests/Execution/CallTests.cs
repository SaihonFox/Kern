using Kern.Bytecode;
using Kern.Tests.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

public sealed class CallTests
{
	[Fact]
	public void Call_PassesArgumentsAndReturnsResult()
	{
		FunctionDef add = TestVm.Function("add",
			new CodeBuilder().Load(0).Load(1).Op(OpCode.Add).Op(OpCode.Ret).Build(),
			paramCount: 2);
		FunctionDef main = TestVm.Function("main",
			new CodeBuilder().Push(2).Push(3).Op(OpCode.Call, 0, 2).Op(OpCode.Ret).Build());

		KValue result = new Interpreter(TestVm.Module(add, main)).Run("main", []);

		Assert.Equal(5, result.AsI32);
	}

	[Fact]
	public void Call_ResultReplacesArgumentsOnTheStack()
	{
		FunctionDef add = TestVm.Function("add",
			new CodeBuilder().Load(0).Load(1).Op(OpCode.Add).Op(OpCode.Ret).Build(),
			paramCount: 2);
		FunctionDef main = TestVm.Function("main",
			new CodeBuilder()
				.Push(100).Push(2).Push(3)
				.Op(OpCode.Call, 0, 2)
				.Op(OpCode.Sub)
				.Op(OpCode.Ret)
				.Build());

		KValue result = new Interpreter(TestVm.Module(add, main)).Run("main", []);

		Assert.Equal(95, result.AsI32);
	}

	[Fact]
	public void NestedCalls_Work()
	{
		FunctionDef twice = TestVm.Function("twice",
			new CodeBuilder().Load(0).Push(2).Op(OpCode.Mul).Op(OpCode.Ret).Build(),
			paramCount: 1);
		FunctionDef plusOne = TestVm.Function("plus_one",
			new CodeBuilder().Load(0).Op(OpCode.Call, 0, 1).Push(1).Op(OpCode.Add).Op(OpCode.Ret).Build(),
			paramCount: 1);
		KbcModule module = TestVm.Module(twice, plusOne);

		KValue result = new Interpreter(module).Run("plus_one", [KValue.I32(4)]);

		Assert.Equal(9, result.AsI32);
	}

	[Fact]
	public void RecursiveCall_ComputesFactorial()
	{
		CodeBuilder code = new();
		code.Load(0).Push(1).Op(OpCode.Le);
		int baseOperand = code.Position + 1;
		code.Jump(OpCode.JmpIfNot, 0);
		code.Push(1).Op(OpCode.Ret);

		int recursive = code.Position;
		code.Patch(baseOperand, recursive);
		code.Load(0).Load(0).Push(1).Op(OpCode.Sub)
			.Op(OpCode.Call, 0, 1)
			.Op(OpCode.Mul)
			.Op(OpCode.Ret);

		FunctionDef factorial = TestVm.Function("factorial", code.Build(), paramCount: 1);
		KbcModule module = TestVm.Module(factorial);

		KValue result = new Interpreter(module).Run("factorial", [KValue.I32(5)]);

		Assert.Equal(120, result.AsI32);
	}

	[Fact]
	public void Call_DoesNotClobberCallerLocals()
	{
		FunctionDef plusOne = TestVm.Function("plus_one",
			new CodeBuilder().Load(0).Push(1).Op(OpCode.Add).Op(OpCode.Ret).Build(),
			paramCount: 1);
		FunctionDef main = TestVm.Function("main",
			new CodeBuilder()
				.Push(10).Store(0)
				.Push(5).Op(OpCode.Call, 0, 1)
				.Load(0).Op(OpCode.Add)
				.Op(OpCode.Ret)
				.Build(),
			localCount: 1);

		KValue result = new Interpreter(TestVm.Module(plusOne, main)).Run("main", []);

		Assert.Equal(16, result.AsI32);
	}

	[Fact]
	public void VoidFunction_LeavesVoidOnStack()
	{
		FunctionDef nothing = TestVm.Function("nothing",
			[(int)OpCode.PushVoid, (int)OpCode.Ret]);
		FunctionDef main = TestVm.Function("main",
			[(int)OpCode.Call, 0, 0, (int)OpCode.Ret]);

		KValue result = new Interpreter(TestVm.Module(nothing, main)).Run("main", []);

		Assert.Equal(ValueKind.Void, result.Kind);
	}

	[Fact]
	public void WrongArity_ByIndexPanics()
	{
		FunctionDef main = TestVm.Function("main", [(int)OpCode.PushVoid, (int)OpCode.Ret]);
		Interpreter interpreter = new(TestVm.Module(main));

		KernPanic error = Assert.Throws<KernPanic>(() => interpreter.Run(0, [KValue.I32(1)]));

		Assert.Contains("ожидает 0 аргументов", error.Message);
	}

	[Fact]
	public void MissingFunction_Panics()
	{
		FunctionDef main = TestVm.Function("main", [(int)OpCode.PushVoid, (int)OpCode.Ret]);
		Interpreter interpreter = new(TestVm.Module(main));

		KernPanic error = Assert.Throws<KernPanic>(() => interpreter.Run("nope", []));

		Assert.Contains("не найдена", error.Message);
	}
}
