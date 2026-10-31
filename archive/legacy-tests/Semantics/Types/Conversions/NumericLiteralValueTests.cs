using System.Numerics;
using Kern.Compiler.Semantics.Types;
using Xunit;

namespace Kern.Tests.Semantics.Types.Conversions;

public sealed class NumericLiteralValueTests
{
	[Theory]
	[InlineData("0", 0)]
	[InlineData("123", 123)]
	[InlineData("0x012DEF", 77295)]
	[InlineData("0b01101010", 106)]
	[InlineData("0h123523", 42835)]
	[InlineData("1_000", 1000)]
	[InlineData("-42", -42)]
	[InlineData("+5", 5)]
	public void Parses_integer_literals_with_radix_separators_and_sign(string text, long expected)
	{
		Assert.True(NumericLiteralValue.TryParse(text, out var value));

		Assert.False(value.IsFloat);
		Assert.Equal(new BigInteger(expected), value.IntegerValue);
		Assert.Null(value.ExactType);
	}

	[Theory]
	[InlineData(".5", 0.5)]
	[InlineData("0.5", 0.5)]
	[InlineData("1.5e2", 150.0)]
	[InlineData("1e3", 1000.0)]
	[InlineData("2.5E-1", 0.25)]
	public void Parses_float_literals_with_fraction_and_exponent(string text, double expected)
	{
		Assert.True(NumericLiteralValue.TryParse(text, out var value));

		Assert.True(value.IsFloat);
		Assert.Equal(expected, value.FloatValue, 12);
		Assert.Null(value.ExactType);
	}

	[Theory]
	[InlineData("5b", PrimitiveKind.U8, false)]
	[InlineData("5i8", PrimitiveKind.I8, false)]
	[InlineData("5i16", PrimitiveKind.I16, false)]
	[InlineData("5i32", PrimitiveKind.I32, false)]
	[InlineData("5i64", PrimitiveKind.I64, false)]
	[InlineData("5u8", PrimitiveKind.U8, false)]
	[InlineData("5u16", PrimitiveKind.U16, false)]
	[InlineData("5u32", PrimitiveKind.U32, false)]
	[InlineData("5u64", PrimitiveKind.U64, false)]
	[InlineData("5f16", PrimitiveKind.F16, true)]
	[InlineData("5f32", PrimitiveKind.F32, true)]
	[InlineData("5f64", PrimitiveKind.F64, true)]
	[InlineData("1.5f32", PrimitiveKind.F32, true)]
	public void Suffixes_set_the_exact_type(string text, PrimitiveKind expected, bool isFloat)
	{
		Assert.True(NumericLiteralValue.TryParse(text, out var value));

		Assert.Equal(expected, value.ExactType);
		Assert.Equal(isFloat, value.IsFloat);
	}

	[Theory]
	[InlineData("255u8")]
	[InlineData("65504f16")]
	public void Suffix_values_inside_their_range_are_accepted(string text)
	{
		Assert.True(NumericLiteralValue.TryParse(text, out _));
	}

	[Fact]
	public void Radix_literals_accept_a_suffix_after_the_digit_run()
	{
		Assert.True(NumericLiteralValue.TryParse("0x10u8", out var hex));
		Assert.Equal(PrimitiveKind.U8, hex.ExactType);
		Assert.Equal(new BigInteger(16), hex.IntegerValue);

		Assert.True(NumericLiteralValue.TryParse("0b101b", out var binary));
		Assert.Equal(PrimitiveKind.U8, binary.ExactType);
		Assert.Equal(new BigInteger(5), binary.IntegerValue);
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("abc")]
	[InlineData("0x")]
	[InlineData("0x_1")]
	[InlineData("0b012")]
	[InlineData("0h78")]
	[InlineData("5.")]
	[InlineData(".")]
	[InlineData("_1")]
	[InlineData("1_")]
	[InlineData("1__0")]
	[InlineData("1e")]
	[InlineData("1e+")]
	[InlineData("5q")]
	[InlineData("5f65")]
	[InlineData("1.5i32")]
	[InlineData("1e3u8")]
	[InlineData("256u8")]
	[InlineData("70000f16")]
	[InlineData("1e999")]
	public void Malformed_or_out_of_range_literals_are_rejected(string text)
	{
		Assert.False(NumericLiteralValue.TryParse(text, out _));
	}

	[Fact]
	public void Explicit_factories_carry_negative_values()
	{
		var value = NumericLiteralValue.FromInteger(-1);

		Assert.False(value.IsFloat);
		Assert.Equal(new BigInteger(-1), value.IntegerValue);
		Assert.Null(value.ExactType);
	}
}
