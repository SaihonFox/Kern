using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Xunit;
using TypeConversions = Kern.Compiler.Semantics.Types.Conversions.Conversions;

namespace Kern.Tests.Semantics.Types.Conversions;

public sealed class NumericConversionTests
{
	private static readonly (PrimitiveKind From, PrimitiveKind[] To)[] WideningChains =
	[
		(PrimitiveKind.I8, [PrimitiveKind.I16, PrimitiveKind.I32, PrimitiveKind.I64, PrimitiveKind.F32, PrimitiveKind.F64]),
		(PrimitiveKind.I16, [PrimitiveKind.I32, PrimitiveKind.I64, PrimitiveKind.F32, PrimitiveKind.F64]),
		(PrimitiveKind.I32, [PrimitiveKind.I64, PrimitiveKind.F32, PrimitiveKind.F64]),
		(PrimitiveKind.I64, [PrimitiveKind.F32, PrimitiveKind.F64]),
		(PrimitiveKind.U8,
			[
				PrimitiveKind.U16, PrimitiveKind.U32, PrimitiveKind.U64,
				PrimitiveKind.I16, PrimitiveKind.I32, PrimitiveKind.I64,
				PrimitiveKind.F32, PrimitiveKind.F64
			]),
		(PrimitiveKind.U16,
			[
				PrimitiveKind.U32, PrimitiveKind.U64,
				PrimitiveKind.I32, PrimitiveKind.I64,
				PrimitiveKind.F32, PrimitiveKind.F64
			]),
		(PrimitiveKind.U32, [PrimitiveKind.U64, PrimitiveKind.I64, PrimitiveKind.F32, PrimitiveKind.F64]),
		(PrimitiveKind.U64, [PrimitiveKind.F32, PrimitiveKind.F64]),
		(PrimitiveKind.F16, [PrimitiveKind.F32, PrimitiveKind.F64]),
		(PrimitiveKind.F32, [PrimitiveKind.F64])
	];

	private static readonly PrimitiveKind[] Integral =
		[PrimitiveKind.I8, PrimitiveKind.I16, PrimitiveKind.I32, PrimitiveKind.I64,
			PrimitiveKind.U8, PrimitiveKind.U16, PrimitiveKind.U32, PrimitiveKind.U64];

	private static readonly PrimitiveKind[] Floating = [PrimitiveKind.F16, PrimitiveKind.F32, PrimitiveKind.F64];

	public static IEnumerable<object[]> WideningPairs() =>
		WideningChains.SelectMany(chain => chain.To.Select(to => new object[] { chain.From, to }));

	[Theory]
	[MemberData(nameof(WideningPairs))]
	public void Every_implicit_numeric_widening_is_classified(PrimitiveKind from, PrimitiveKind to)
	{
		var conversion = ConversionTestSupport.Classify(PrimitiveType.For(from), PrimitiveType.For(to));

		Assert.Equal(ConversionKind.NumericWidening, conversion.Kind);
	}

	[Theory]
	[InlineData(PrimitiveKind.I8, PrimitiveKind.Inum)]
	[InlineData(PrimitiveKind.U64, PrimitiveKind.Inum)]
	[InlineData(PrimitiveKind.I32, PrimitiveKind.Num)]
	[InlineData(PrimitiveKind.F32, PrimitiveKind.Fnum)]
	[InlineData(PrimitiveKind.F64, PrimitiveKind.Num)]
	[InlineData(PrimitiveKind.Inum, PrimitiveKind.Num)]
	[InlineData(PrimitiveKind.Fnum, PrimitiveKind.Num)]
	public void Universal_numeric_types_accept_their_family(PrimitiveKind from, PrimitiveKind to)
	{
		var conversion = ConversionTestSupport.Classify(PrimitiveType.For(from), PrimitiveType.For(to));

		Assert.Equal(ConversionKind.NumericWidening, conversion.Kind);
	}

	[Fact]
	public void No_numeric_conversion_exists_outside_the_csharp_table()
	{
		var accepted = BuildAccepted();

		foreach (var from in Enum.GetValues<PrimitiveKind>())
		{
			if (from is PrimitiveKind.Never)
				continue;

			foreach (var to in Enum.GetValues<PrimitiveKind>())
			{
				// object — отдельное правило упаковки, а не числовое преобразование.
				if (to is PrimitiveKind.Object)
					continue;

				var expected = accepted.Contains((from, to));
				var actual = TypeConversions.TryClassify(PrimitiveType.For(from), PrimitiveType.For(to), out var conversion);
				Assert.True(expected == actual, $"{from} -> {to}: ожидалось {expected}, получено {actual}");
				if (expected && from != to)
					Assert.Equal(ConversionKind.NumericWidening, conversion.Kind);
			}
		}
	}

	[Theory]
	[InlineData(PrimitiveKind.I16, PrimitiveKind.I8)]
	[InlineData(PrimitiveKind.I64, PrimitiveKind.I32)]
	[InlineData(PrimitiveKind.U64, PrimitiveKind.I64)]
	[InlineData(PrimitiveKind.I64, PrimitiveKind.U64)]
	[InlineData(PrimitiveKind.F64, PrimitiveKind.F32)]
	[InlineData(PrimitiveKind.F32, PrimitiveKind.I32)]
	[InlineData(PrimitiveKind.F64, PrimitiveKind.I64)]
	[InlineData(PrimitiveKind.I32, PrimitiveKind.U32)]
	[InlineData(PrimitiveKind.U32, PrimitiveKind.I32)]
	[InlineData(PrimitiveKind.U8, PrimitiveKind.I8)]
	[InlineData(PrimitiveKind.I8, PrimitiveKind.U8)]
	[InlineData(PrimitiveKind.I64, PrimitiveKind.F16)]
	[InlineData(PrimitiveKind.Char, PrimitiveKind.I32)]
	[InlineData(PrimitiveKind.I32, PrimitiveKind.Char)]
	[InlineData(PrimitiveKind.Bool, PrimitiveKind.I32)]
	[InlineData(PrimitiveKind.I32, PrimitiveKind.Bool)]
	public void Narrowing_and_non_numeric_pairs_are_rejected(PrimitiveKind from, PrimitiveKind to)
	{
		ConversionTestSupport.Rejects(PrimitiveType.For(from), PrimitiveType.For(to));
	}

	[Fact]
	public void Int_to_float_follows_csharp_even_when_precision_is_lost()
	{
		// C# делает long/ulong -> float и int -> float неявными; задание перечисляет i64->f32/f64.
		Assert.Equal(ConversionKind.NumericWidening, ConversionTestSupport.Classify(PrimitiveType.I64, PrimitiveType.F32).Kind);
		Assert.Equal(ConversionKind.NumericWidening, ConversionTestSupport.Classify(PrimitiveType.U64, PrimitiveType.F32).Kind);
		Assert.Equal(ConversionKind.NumericWidening, ConversionTestSupport.Classify(PrimitiveType.I32, PrimitiveType.F32).Kind);
		ConversionTestSupport.Rejects(PrimitiveType.U16, PrimitiveType.I16);
	}

	[Fact]
	public void Identity_is_reported_for_equal_numeric_types()
	{
		var conversion = ConversionTestSupport.Classify(PrimitiveType.I32, PrimitiveType.I32);

		Assert.Equal(ConversionKind.Identity, conversion.Kind);
		Assert.Equal(0, conversion.Rank);
	}

	private static HashSet<(PrimitiveKind From, PrimitiveKind To)> BuildAccepted()
	{
		var accepted = new HashSet<(PrimitiveKind, PrimitiveKind)>();
		foreach (var kind in Enum.GetValues<PrimitiveKind>())
			accepted.Add((kind, kind));

		foreach (var (from, targets) in WideningChains)
			foreach (var to in targets)
				accepted.Add((from, to));

		foreach (var integral in Integral)
		{
			accepted.Add((integral, PrimitiveKind.Inum));
			accepted.Add((integral, PrimitiveKind.Num));
		}

		foreach (var floating in Floating)
		{
			accepted.Add((floating, PrimitiveKind.Fnum));
			accepted.Add((floating, PrimitiveKind.Num));
		}

		accepted.Add((PrimitiveKind.Inum, PrimitiveKind.Num));
		accepted.Add((PrimitiveKind.Fnum, PrimitiveKind.Num));
		return accepted;
	}
}
