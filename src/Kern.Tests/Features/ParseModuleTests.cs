using Kern.Tests.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary><c>kern.parse</c>: идентификаторы, числа, строки и диагностика с позицией.</summary>
[Collection("timing")]
public sealed class ParseModuleTests
{
	[Fact]
	public void Identifiers_and_predicate()
	{
		const string source = """
			use kern.parse[is_ident]
			fun good() -> bool is_ident("hello_1")
			fun bad() -> bool is_ident("1bad")
			""";

		Assert.Equal(KValue.Bool(true), StdSourcePipeline.Run(source, "good"));
		Assert.Equal(KValue.Bool(false), StdSourcePipeline.Run(source, "bad"));
	}

	[Fact]
	public void Numbers_are_parsed_with_invariant_culture()
	{
		const string source = """
			use kern.parse[parse_integer, parse_number]
			fun integer() -> i64 {
				match parse_integer("-42") {
					Ok(value) => value
					Err(_) => 0
				}
			}

			fun number() -> f64 {
				match parse_number("1.5") {
					Ok(value) => value
					Err(_) => 0.0
				}
			}
			""";

		Assert.Equal(KValue.I64(-42), StdSourcePipeline.Run(source, "integer"));
		Assert.Equal(1.5, (double)StdSourcePipeline.Run(source, "number").AsF64);
	}

	[Fact]
	public void Quoted_strings_decode_escapes()
	{
		const string source = """
			use kern.parse[parse_quoted]
			fun text() -> str {
				match parse_quoted("\"a\\nb\"") {
					Ok(value) => value
					Err(_) => ""
				}
			}

			fun unicode() -> str {
				match parse_quoted("\"\\u0041\"") {
					Ok(value) => value
					Err(_) => ""
				}
			}
			""";

		Assert.Equal(KValue.Str("a\nb"), StdSourcePipeline.Run(source, "text"));
		Assert.Equal(KValue.Str("A"), StdSourcePipeline.Run(source, "unicode"));
	}

	[Fact]
	public void Error_message_has_line_column_and_caret()
	{
		const string source = """
			use kern.parse[parse_error]
			fun message() -> str parse_error("line1\nline2", 7)
			""";

		string text = StdSourcePipeline.Run(source, "message").AsStr;
		Assert.Contains("2:2:", text);
		Assert.Contains("line2", text);
		Assert.Contains("^", text);
	}
}
