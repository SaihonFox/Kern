using Kern.Compiler.Lexing;
using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>Префиксы строк <c>r</c>/<c>f</c>/<c>x</c> можно писать в любом порядке.</summary>
[Collection("timing")]
public sealed class StringPrefixTests
{
	[Fact]
	public void Raw_interpolation_prefixes_are_order_independent()
	{
		const string source = """
			fun rf() -> str rf"a\n${1 + 1}"
			fun fr() -> str fr"a\n${1 + 1}"
			""";

		Assert.Equal(KValue.Str("a\\n2"), StdPipeline.Run(source, "rf"));
		Assert.Equal(KValue.Str("a\\n2"), StdPipeline.Run(source, "fr"));
	}

	[Fact]
	public void Regex_prefixes_are_order_independent()
	{
		const string source = """
			fun rx() -> bool rx"\d+".is_match("123")
			fun xr() -> bool xr"\d+".is_match("abc")
			fun xrf() -> bool xrf"\d+".is_match("123")
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "rx"));
		Assert.Equal(KValue.Bool(false), StdPipeline.Run(source, "xr"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "xrf"));
	}

	[Fact]
	public void Regex_interpolation_prefixes_work_in_any_order()
	{
		const string source = """
			fun fx() -> bool fx"a${"b"}c".is_match("abc")
			fun xf() -> bool xf"a${"b"}c".is_match("abc")
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "fx"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "xf"));
	}

	[Fact]
	public void Duplicate_and_contradictory_prefixes_are_rejected()
	{
		Assert.False(StringLiteralText.TryReadHeader("ff\"x\"", out _, out _));
		Assert.False(StringLiteralText.TryReadHeader("rv\"x\"", out _, out _));
		Assert.False(StringLiteralText.TryReadHeader("vr\"x\"", out _, out _));
	}

	[Fact]
	public void Header_flags_match_the_prefix_in_any_order()
	{
		Assert.True(StringLiteralText.TryReadHeader("xrf\"p\"", out var header, out _));
		Assert.True(header.Interpolated);
		Assert.True(header.Raw);
		Assert.True(header.Regex);
	}
}
