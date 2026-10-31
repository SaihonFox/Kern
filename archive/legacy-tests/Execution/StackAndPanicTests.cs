using Kern.Bytecode;
using Kern.Tests.Bytecode;
using Kern.Vm.Execution;
using Xunit;

namespace Kern.Tests.Execution;

public sealed class StackAndPanicTests
{
	[Fact]
	public void Ret_WithExtraValues_PanicsOnStackBalance()
	{
		int[] code = new CodeBuilder().Push(1).Push(2).Op(OpCode.Ret).Build();

		KernPanic error = Assert.Throws<KernPanic>(() => TestVm.Run(code));

		Assert.Contains("баланс стека", error.Message);
	}

	[Fact]
	public void Pop_OnEmptyStack_Panics()
	{
		int[] code = new CodeBuilder().Op(OpCode.Pop).Op(OpCode.PushVoid).Op(OpCode.Ret).Build();

		KernPanic error = Assert.Throws<KernPanic>(() => TestVm.Run(code));

		Assert.Contains("пустой стек", error.Message);
	}

	[Fact]
	public void Panic_InsideCall_ContainsStackTrace()
	{
		FunctionDef inner = TestVm.Function("inner",
			new CodeBuilder().Load(0).Push(0).Op(OpCode.Div).Op(OpCode.Ret).Build(),
			paramCount: 1);
		inner.LineTable = [new LineEntry(4, 7)];

		FunctionDef outer = TestVm.Function("outer",
			new CodeBuilder().Push(10).Op(OpCode.Call, 0, 1).Op(OpCode.Ret).Build());
		outer.LineTable = [new LineEntry(2, 3)];

		KernPanic error = Assert.Throws<KernPanic>(
			() => new Interpreter(TestVm.Module(inner, outer)).Run("outer", []));

		Assert.Equal("деление на ноль", error.Message);
		Assert.Equal(2, error.Frames.Count);
		Assert.Contains("inner", error.Frames[0]);
		Assert.Contains("строка 7", error.Frames[0]);
		Assert.Contains("outer", error.Frames[1]);
		Assert.Contains("строка 3", error.Frames[1]);
		Assert.Contains("panic: деление на ноль", error.Describe());
	}

	[Fact]
	public void Panic_WithoutLineTable_ReportsOffset()
	{
		FunctionDef main = TestVm.Function("main",
			new CodeBuilder().Push(1).Push(0).Op(OpCode.Div).Op(OpCode.Ret).Build());

		KernPanic error = Assert.Throws<KernPanic>(
			() => new Interpreter(TestVm.Module(main)).Run("main", []));

		Assert.Contains("смещение", error.Frames[0]);
	}

	[Fact]
	public void RestoreSp_RejectsDepthOutsideStack()
	{
		int[] code = new CodeBuilder()
			.Push(99)
			.Store(0)
			.Op(OpCode.RestoreSp, 0)
			.Op(OpCode.Ret)
			.Build();

		KernPanic error = Assert.Throws<KernPanic>(() => TestVm.Run(code, localCount: 1));

		Assert.Contains("недопустимая глубина", error.Message);
	}

	[Fact]
	public void Interpreter_RejectsInvalidModule()
	{
		FunctionDef main = TestVm.Function("main", [(int)OpCode.PushConst, 5, (int)OpCode.Ret]);
		KbcModule module = TestVm.Module(main);

		Assert.Throws<KbcFormatException>(() => new Interpreter(module));
	}
}
