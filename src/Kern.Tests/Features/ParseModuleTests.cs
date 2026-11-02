using Kern.Tests.Features.Support;
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
			fun good() -> bool is_ident("hello_1")
			fun bad() -> bool is_ident("1bad")
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "good"));
		Assert.Equal(KValue.Bool(false), StdPipeline.Run(source, "bad"));
	}

	[Fact]
	public void Numbers_are_parsed_with_invariant_culture()
	{
		const string source = """
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

		Assert.Equal(KValue.I64(-42), StdPipeline.Run(source, "integer"));
		Assert.Equal(1.5, (double)StdPipeline.Run(source, "number").AsF64);
	}

	[Fact]
	public void Quoted_strings_decode_escapes()
	{
		const string source = """
			fun text() -> str {
				match parse_quoted("\"a\\nb\"") {
					Ok(value) => value
					Err(_) => ""
				}
			}
			""";

		Assert.Equal(KValue.Str("a\nb"), StdPipeline.Run(source, "text"));
	}

	[Fact]
	public void Error_message_has_line_column_and_caret()
	{
		const string source = """
			fun message() -> str parse_error("line1\nline2", 7)
			""";

		string text = StdPipeline.Run(source, "message").AsStr;
		Assert.Contains("2:2:", text);
		Assert.Contains("line2", text);
		Assert.Contains("^", text);
	}
}
