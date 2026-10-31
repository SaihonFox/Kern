using Kern.Bytecode;
using Kern.Tests.Bytecode;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

public sealed class ControlFlowTests
{
	[Theory]
	[InlineData(true, 10)]
	[InlineData(false, 20)]
	public void ConditionalJump_SelectsBranch(bool condition, int expected)
	{
		CodeBuilder code = new();
		code.PushBool(condition);
		int operand = code.Position + 1;
		code.Jump(OpCode.JmpIfNot, 0);
		code.Push(10).Op(OpCode.Ret);
		int elseTarget = code.Position;
		code.Patch(operand, elseTarget);
		code.Push(20).Op(OpCode.Ret);

		Assert.Equal(expected, TestVm.Run(code.Build()).AsI32);
	}

	[Fact]
	public void JmpIf_JumpsWhenTrue()
	{
		CodeBuilder code = new();
		code.PushBool(true);
		int operand = code.Position + 1;
		code.Jump(OpCode.JmpIf, 0);
		code.Push(1).Op(OpCode.Ret);
		int target = code.Position;
		code.Patch(operand, target);
		code.Push(2).Op(OpCode.Ret);

		Assert.Equal(2, TestVm.Run(code.Build()).AsI32);
	}

	[Fact]
	public void Jmp_SkipsCode()
	{
		CodeBuilder code = new();
		code.Push(1);
		int operand = code.Position + 1;
		code.Jump(OpCode.Jmp, 0);
		code.Push(2);
		int target = code.Position;
		code.Patch(operand, target);
		code.Op(OpCode.Ret);

		Assert.Equal(1, TestVm.Run(code.Build()).AsI32);
	}

	[Fact]
	public void Loop_WithJumpsAndLocals_SumsOneToTen()
	{
		CodeBuilder code = new();
		code.Push(1).Store(0);          // i = 1
		code.Push(0).Store(1);          // sum = 0

		int loopStart = code.Position;
		code.Load(0).Push(10).Op(OpCode.Le);
		int exitOperand = code.Position + 1;
		code.Jump(OpCode.JmpIfNot, 0);

		code.Load(1).Load(0).Op(OpCode.Add).Store(1);
		code.Load(0).Push(1).Op(OpCode.Add).Store(0);
		code.Jump(OpCode.Jmp, loopStart);

		int exit = code.Position;
		code.Patch(exitOperand, exit);
		code.Load(1).Op(OpCode.Ret);

		KValue result = TestVm.Run(code.Build(), localCount: 2);

		Assert.Equal(55, result.AsI32);
	}

	[Fact]
	public void SaveAndRestoreSp_DropsIntermediateValues()
	{
		int[] code = new CodeBuilder()
			.Push(7)
			.Op(OpCode.SaveSp, 0)
			.Push(42)
			.Op(OpCode.RestoreSp, 0)
			.Op(OpCode.Ret)
			.Build();

		Assert.Equal(7, TestVm.Run(code, localCount: 1).AsI32);
	}

	[Fact]
	public void RestoreSp_RejectsWrongLocalKind()
	{
		int[] code = new CodeBuilder()
			.Push(123)
			.Store(0)
			.Op(OpCode.RestoreSp, 0)
			.Op(OpCode.Ret)
			.Build();

		Assert.Throws<Kern.Vm.Execution.KernPanic>(() => TestVm.Run(code, localCount: 1));
	}

	[Fact]
	public void Stop_ReturnsVoid()
	{
		KValue result = TestVm.Run(new CodeBuilder().Push(1).Op(OpCode.Stop).Build());

		Assert.Equal(ValueKind.Void, result.Kind);
	}
}
