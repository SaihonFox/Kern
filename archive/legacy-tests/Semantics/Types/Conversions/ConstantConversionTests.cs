using System.Numerics;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Xunit;
using TypeConversions = Kern.Compiler.Semantics.Types.Conversions.Conversions;

namespace Kern.Tests.Semantics.Types.Conversions;

public sealed class ConstantConversionTests
{
	[Theory]
	[InlineData("5", PrimitiveKind.U8)]
	[InlineData("5", PrimitiveKind.I8)]
	[InlineData("5", PrimitiveKind.I64)]
	[InlineData("5", PrimitiveKind.F64)]
	[InlineData("5", PrimitiveKind.F32)]
	[InlineData("-128", PrimitiveKind.I8)]
	[InlineData("0", PrimitiveKind.U8)]
	[InlineData("65504", PrimitiveKind.F16)]
	[InlineData("18446744073709551615", PrimitiveKind.U64)]
	public void Unsuffixed_integer_fits_the_target(string text, PrimitiveKind target)
	{
		var conversion = Classify(text, target);

		Assert.Equal(ConversionKind.NumericConstant, conversion.Kind);
		Assert.Equal(1, conversion.Rank);
	}

	[Theory]
	[InlineData("300", PrimitiveKind.U8)]
	[InlineData("200", PrimitiveKind.I8)]
	[InlineData("128", PrimitiveKind.I8)]
	[InlineData("-1", PrimitiveKind.U32)]
	[InlineData("65536", PrimitiveKind.U16)]
	[InlineData("18446744073709551616", PrimitiveKind.U64)]
	[InlineData("70000", PrimitiveKind.F16)]
	[InlineData("5", PrimitiveKind.Char)]
	[InlineData("5", PrimitiveKind.Bool)]
	[InlineData("5", PrimitiveKind.Str)]
	[InlineData("5", PrimitiveKind.Void)]
	public void Unsuffixed_integer_outside_the_range_is_rejected(string text, PrimitiveKind target)
	{
		Rejects(text, target);
	}

	[Theory]
	[InlineData("18446744073709551616", PrimitiveKind.F64)]
	[InlineData("18446744073709551616", PrimitiveKind.F32)]
	[InlineData("-9223372036854775809", PrimitiveKind.F64)]
	public void Integer_literal_outside_i64_u64_range_is_not_a_valid_literal(string text, PrimitiveKind target)
	{
		Rejects(text, target);
	}

	[Fact]
	public void Negative_values_come_from_the_folded_sign()
	{
		// Отрицательный литерал в Kern — унарный минус; binder складывает его в значение.
		var negative = NumericLiteralValue.FromInteger(-1);

		Assert.False(TypeConversions.TryClassifyConstant(negative, PrimitiveKind.U32, out _));
		Assert.True(TypeConversions.TryClassifyConstant(negative, PrimitiveKind.I32, out var conversion));
		Assert.Equal(ConversionKind.NumericConstant, conversion.Kind);
	}

	[Theory]
	[InlineData("1.5", PrimitiveKind.F16)]
	[InlineData("1.5", PrimitiveKind.F32)]
	[InlineData("1.5", PrimitiveKind.F64)]
	public void Unsuffixed_float_fits_float_targets(string text, PrimitiveKind target)
	{
		var conversion = Classify(text, target);

		Assert.Equal(ConversionKind.NumericConstant, conversion.Kind);
	}

	[Theory]
	[InlineData("1.5", PrimitiveKind.I32)]
	[InlineData("5.0", PrimitiveKind.I32)]
	[InlineData("1.5", PrimitiveKind.U8)]
	[InlineData("1.5", PrimitiveKind.Inum)]
	[InlineData("5", PrimitiveKind.Fnum)]
	public void Unsuffixed_float_never_converts_to_integer_targets(string text, PrimitiveKind target)
	{
		Rejects(text, target);
	}

	[Theory]
	[InlineData("5f64", PrimitiveKind.I32)]
	[InlineData("5f64", PrimitiveKind.F32)]
	[InlineData("5i32", PrimitiveKind.I8)]
	[InlineData("5u16", PrimitiveKind.U8)]
	public void Suffixed_literals_do_not_use_constant_conversion(string text, PrimitiveKind target)
	{
		Rejects(text, target);
	}

	[Theory]
	[InlineData("5f64", PrimitiveKind.F64, ConversionKind.Identity)]
	[InlineData("5f32", PrimitiveKind.F64, ConversionKind.NumericWidening)]
	[InlineData("5i32", PrimitiveKind.I64, ConversionKind.NumericWidening)]
	[InlineData("5b", PrimitiveKind.U8, ConversionKind.Identity)]
	[InlineData("5b", PrimitiveKind.I16, ConversionKind.NumericWidening)]
	public void Suffixed_literals_only_widen_from_their_exact_type(
		string text,
		PrimitiveKind target,
		ConversionKind expected)
	{
		var conversion = Classify(text, target);

		Assert.Equal(expected, conversion.Kind);
	}

	[Theory]
	[InlineData("5", PrimitiveKind.Inum)]
	[InlineData("5", PrimitiveKind.Num)]
	[InlineData("1.5", PrimitiveKind.Fnum)]
	[InlineData("1.5", PrimitiveKind.Num)]
	public void Universal_numeric_targets_accept_literals_of_their_family(string text, PrimitiveKind target)
	{
		var conversion = Classify(text, target);

		Assert.Equal(ConversionKind.NumericConstant, conversion.Kind);
	}

	[Fact]
	public void Value_out_of_range_for_an_explicit_type_is_rejected_by_the_parser()
	{
		Assert.False(NumericLiteralValue.TryParse("256u8", out _));
		Assert.True(NumericLiteralValue.TryParse("255u8", out var value));
		Assert.Equal(new BigInteger(255), value.IntegerValue);
	}

	private static Conversion Classify(string text, PrimitiveKind target)
	{
		Assert.True(NumericLiteralValue.TryParse(text, out var literal), $"Литерал '{text}' не разобран");
		Assert.True(
			TypeConversions.TryClassifyConstant(literal, target, out var conversion),
			$"Ожидалось константное преобразование '{text}' -> {target}");
		Assert.True(conversion.IsImplicit);
		return conversion;
	}

	private static void Rejects(string text, PrimitiveKind target)
	{
		Assert.True(NumericLiteralValue.TryParse(text, out var literal), $"Литерал '{text}' не разобран");
		Assert.False(
			TypeConversions.TryClassifyConstant(literal, target, out _),
			$"Не ожидалось константного преобразования '{text}' -> {target}");
	}
}
