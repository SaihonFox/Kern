using Kern.Bytecode;
using Kern.Tests.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

public sealed class ArithmeticTests
{
	[Fact]
	public void I32_AddSubMul()
	{
		Assert.Equal(5, I32(new CodeBuilder().Push(2).Push(3).Op(OpCode.Add)));
		Assert.Equal(-1, I32(new CodeBuilder().Push(2).Push(3).Op(OpCode.Sub)));
		Assert.Equal(-6, I32(new CodeBuilder().Push(-2).Push(3).Op(OpCode.Mul)));
	}

	[Fact]
	public void I32_WrapsOnOverflow()
	{
		Assert.Equal(int.MinValue, I32(new CodeBuilder().Push(int.MaxValue).Push(1).Op(OpCode.Add)));
		Assert.Equal(int.MinValue, I32(new CodeBuilder().Push(int.MinValue).Op(OpCode.Neg)));
	}

	[Fact]
	public void I32_DivisionTruncatesTowardZero()
	{
		Assert.Equal(-3, I32(new CodeBuilder().Push(-7).Push(2).Op(OpCode.Div)));
		Assert.Equal(-1, I32(new CodeBuilder().Push(-7).Push(3).Op(OpCode.Mod)));
		Assert.Equal(1, I32(new CodeBuilder().Push(7).Push(-3).Op(OpCode.Mod)));
	}

	[Fact]
	public void I32_DivisionByZeroPanics()
	{
		KernPanic division = Assert.Throws<KernPanic>(
			() => I32(new CodeBuilder().Push(1).Push(0).Op(OpCode.Div)));
		Assert.Contains("деление на ноль", division.Message);

		KernPanic modulo = Assert.Throws<KernPanic>(
			() => I32(new CodeBuilder().Push(1).Push(0).Op(OpCode.Mod)));
		Assert.Contains("остаток от деления на ноль", modulo.Message);
	}

	[Fact]
	public void I32_MinValueDivisionByMinusOneWraps()
	{
		Assert.Equal(int.MinValue,
			I32(new CodeBuilder().Push(int.MinValue).Push(-1).Op(OpCode.Div)));
		Assert.Equal(0,
			I32(new CodeBuilder().Push(int.MinValue).Push(-1).Op(OpCode.Mod)));
	}

	[Fact]
	public void I32_PowAndNegativeExponent()
	{
		Assert.Equal(1024, I32(new CodeBuilder().Push(2).Push(10).Op(OpCode.Pow)));
		Assert.Equal(1, I32(new CodeBuilder().Push(0).Push(0).Op(OpCode.Pow)));

		KernPanic error = Assert.Throws<KernPanic>(
			() => I32(new CodeBuilder().Push(2).Push(-1).Op(OpCode.Pow)));
		Assert.Contains("отрицательная степень", error.Message);
	}

	[Fact]
	public void I64_ArithmeticAndWrap()
	{
		KValue sum = TestVm.Run(new CodeBuilder().PushI64(5).PushI64(7).Op(OpCode.Add).Op(OpCode.Ret).Build());
		Assert.Equal(ValueKind.I64, sum.Kind);
		Assert.Equal(12L, sum.AsI64);

		KValue wrap = TestVm.Run(
			new CodeBuilder().PushI64(long.MaxValue).PushI64(1).Op(OpCode.Add).Op(OpCode.Ret).Build());
		Assert.Equal(long.MinValue, wrap.AsI64);

		KValue minDiv = TestVm.Run(
			new CodeBuilder().PushI64(long.MinValue).PushI64(-1).Op(OpCode.Div).Op(OpCode.Ret).Build());
		Assert.Equal(long.MinValue, minDiv.AsI64);
	}

	[Fact]
	public void F64_Arithmetic()
	{
		Assert.Equal(3.75, F64(new CodeBuilder().PushF64(1.5).PushF64(2.25).Op(OpCode.Add)));
		Assert.Equal(1024.0, F64(new CodeBuilder().PushF64(2.0).PushF64(10.0).Op(OpCode.Pow)));
		Assert.Equal(1.5, F64(new CodeBuilder().PushF64(7.5).PushF64(2.0).Op(OpCode.Mod)));
		Assert.Equal(-2.5, F64(new CodeBuilder().PushF64(2.5).Op(OpCode.Neg)));
	}

	[Fact]
	public void F64_DivisionByZeroIsInfinity()
	{
		double value = F64(new CodeBuilder().PushF64(1.0).PushF64(0.0).Op(OpCode.Div));
		Assert.True(double.IsPositiveInfinity(value));
	}

	[Fact]
	public void MixedOperandKindsPanic()
	{
		KernPanic error = Assert.Throws<KernPanic>(
			() => TestVm.Run(new CodeBuilder().Push(1).PushF64(1.0).Op(OpCode.Add).Op(OpCode.Ret).Build()));

		Assert.Contains("несовпадение типов", error.Message);
	}

	[Fact]
	public void NegOnUnsignedPanics()
	{
		KernPanic error = Assert.Throws<KernPanic>(
			() => TestVm.Run(new CodeBuilder().Push(1).Op(OpCode.ConvU32).Op(OpCode.Neg).Op(OpCode.Ret).Build()));

		Assert.Contains("унарный минус", error.Message);
	}

	[Theory]
	[InlineData(OpCode.Eq, 3, 3, true)]
	[InlineData(OpCode.Eq, 3, 4, false)]
	[InlineData(OpCode.Ne, 3, 4, true)]
	[InlineData(OpCode.Ne, 3, 3, false)]
	[InlineData(OpCode.Lt, 3, 4, true)]
	[InlineData(OpCode.Lt, 4, 3, false)]
	[InlineData(OpCode.Le, 3, 3, true)]
	[InlineData(OpCode.Gt, 4, 3, true)]
	[InlineData(OpCode.Ge, 3, 3, true)]
	[InlineData(OpCode.Ge, 2, 3, false)]
	public void I32_Comparisons(OpCode op, int left, int right, bool expected)
	{
		KValue result = TestVm.Run(
			new CodeBuilder().Push(left).Push(right).Op(op).Op(OpCode.Ret).Build());

		Assert.Equal(ValueKind.Bool, result.Kind);
		Assert.Equal(expected, result.AsBool);
	}

	[Fact]
	public void StringComparisons_AreOrdinal()
	{
		KConst[] constants = [KConst.Str("abc"), KConst.Str("abd"), KConst.Str("abc")];

		Assert.True(TestVm.RunWithConsts(constants, new CodeBuilder()
			.Op(OpCode.PushConst, 0).Op(OpCode.PushConst, 2).Op(OpCode.Eq).Op(OpCode.Ret).Build()).AsBool);
		Assert.True(TestVm.RunWithConsts(constants, new CodeBuilder()
			.Op(OpCode.PushConst, 0).Op(OpCode.PushConst, 1).Op(OpCode.Lt).Op(OpCode.Ret).Build()).AsBool);
		Assert.False(TestVm.RunWithConsts(constants, new CodeBuilder()
			.Op(OpCode.PushConst, 0).Op(OpCode.PushConst, 1).Op(OpCode.Ge).Op(OpCode.Ret).Build()).AsBool);
	}

	[Fact]
	public void CharComparisons()
	{
		KConst[] constants = [KConst.Char('a'), KConst.Char('b')];

		Assert.True(TestVm.RunWithConsts(constants, new CodeBuilder()
			.Op(OpCode.PushConst, 0).Op(OpCode.PushConst, 1).Op(OpCode.Lt).Op(OpCode.Ret).Build()).AsBool);
	}

	[Fact]
	public void LogicOperations()
	{
		Assert.False(TestVm.Run(new CodeBuilder().PushBool(true).Op(OpCode.Not).Op(OpCode.Ret).Build()).AsBool);
		Assert.False(TestVm.Run(new CodeBuilder().PushBool(true).PushBool(false)
			.Op(OpCode.And).Op(OpCode.Ret).Build()).AsBool);
		Assert.True(TestVm.Run(new CodeBuilder().PushBool(false).PushBool(true)
			.Op(OpCode.Or).Op(OpCode.Ret).Build()).AsBool);
	}

	[Fact]
	public void LogicOnNonBoolPanics()
	{
		KernPanic error = Assert.Throws<KernPanic>(
			() => TestVm.Run(new CodeBuilder().Push(1).Op(OpCode.Not).Op(OpCode.Ret).Build()));
		Assert.Contains("ожидалось bool", error.Message);
	}

	[Fact]
	public void Conversions()
	{
		Assert.Equal(7.5, F64(new CodeBuilder().Push(7).Op(OpCode.ConvF64).PushF64(0.5).Op(OpCode.Add)));
		Assert.Equal(3, I32(new CodeBuilder().PushF64(3.9).Op(OpCode.ConvI32)));

		KValue u8 = TestVm.Run(new CodeBuilder().Push(321).Op(OpCode.ConvU8).Op(OpCode.Ret).Build());
		Assert.Equal(ValueKind.U8, u8.Kind);
		Assert.Equal((byte)65, u8.AsU8);

		KValue ch = TestVm.Run(new CodeBuilder().Push(65).Op(OpCode.ConvChar).Op(OpCode.Ret).Build());
		Assert.Equal('A', ch.AsChar);

		KValue code = TestVm.RunWithConsts([KConst.Char('B')],
			new CodeBuilder().Op(OpCode.PushConst, 0).Op(OpCode.ConvI32).Op(OpCode.Ret).Build());
		Assert.Equal(66, code.AsI32);

		Assert.True(TestVm.Run(new CodeBuilder().Push(1).Op(OpCode.ConvBool).Op(OpCode.Ret).Build()).AsBool);
		Assert.False(TestVm.Run(new CodeBuilder().Push(0).Op(OpCode.ConvBool).Op(OpCode.Ret).Build()).AsBool);
	}

	[Fact]
	public void InvalidConversionsPanic()
	{
		Assert.Throws<KernPanic>(() =>
			TestVm.RunWithConsts([KConst.Str("x")],
				new CodeBuilder().Op(OpCode.PushConst, 0).Op(OpCode.ConvI32).Op(OpCode.Ret).Build()));
		Assert.Throws<KernPanic>(() =>
			TestVm.Run(new CodeBuilder().PushBool(true).Op(OpCode.ConvI32).Op(OpCode.Ret).Build()));
	}

	[Fact]
	public void Constants_ProduceExactValues()
	{
		Assert.Equal(ValueKind.Nil, Const(KConst.Nil).Kind);
		Assert.Equal(ValueKind.None, Const(KConst.None).Kind);
		Assert.Equal(ValueKind.Void, Const(KConst.Void).Kind);
		Assert.True(Const(KConst.Bool(true)).AsBool);
		Assert.Equal(42L, Const(KConst.I64(42)).AsI64);
		Assert.Equal(1.5, Const(KConst.F64(1.5)).AsF64);
		Assert.Equal('Ж', Const(KConst.Char('Ж')).AsChar);
		Assert.Equal("привет", Const(KConst.Str("привет")).AsStr);
	}

	[Fact]
	public void StackOperations()
	{
		Assert.Equal(14, I32(new CodeBuilder().Push(7).Op(OpCode.Dup).Op(OpCode.Add)));
		Assert.Equal(1, I32(new CodeBuilder().Push(1).Push(2).Op(OpCode.Swap).Op(OpCode.Sub)));
		Assert.Equal(1, I32(new CodeBuilder().Push(1).Push(2).Op(OpCode.Pop)));
	}

	private static KValue Const(KConst value) =>
		TestVm.RunWithConsts([value], new CodeBuilder().Op(OpCode.PushConst, 0).Op(OpCode.Ret).Build());

	private static int I32(CodeBuilder code)
	{
		code.Op(OpCode.Ret);
		KValue value = TestVm.Run(code.Build());
		Assert.Equal(ValueKind.I32, value.Kind);
		return value.AsI32;
	}

	private static double F64(CodeBuilder code)
	{
		code.Op(OpCode.Ret);
		KValue value = TestVm.Run(code.Build());
		Assert.Equal(ValueKind.F64, value.Kind);
		return value.AsF64;
	}
}
