using Kern.Compiler.Formatting;
using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>M11: <c>kernc fmt</c> — табы/CRLF, хвостовые пробелы, идемпотентность и сохранение токенов.</summary>
public sealed class FormatterTests
{
	[Fact]
	public void Format_normalizes_tabs_line_endings_and_trailing_spaces()
	{
		const string source = "fun main {   \n    println(\"ok\")   \n\n\n}\n";
		string formatted = SourceFormatter.Format(source);

		Assert.Contains("\tprintln(\"ok\")\r\n", formatted);
		Assert.DoesNotContain("\n\n\n", formatted);
		Assert.DoesNotContain("   \r\n", formatted);
		Assert.EndsWith("}\r\n", formatted);
		Assert.Equal(formatted, SourceFormatter.Format(formatted));
	}

	[Fact]
	public void Format_preserves_tokens_and_parses_cleanly()
	{
		const string source = "fun f(i32 n) -> i32 {\n    if n > 0 {\n        n * 2\n    } else {\n        0\n    }\n}\n";
		string formatted = SourceFormatter.Format(source);

		Assert.Empty(Parser.Parse(formatted).Diagnostics);
		Assert.Equal("fun f(i32 n) -> i32 {", formatted.Split("\r\n")[0]);
		Assert.DoesNotContain("    ", formatted);
	}

	[Fact]
	public void Format_does_not_touch_a_multiline_string_whose_content_changes()
	{
		const string source = "fun text() -> str \"\"\"\nстрока   \n\"\"\"\n";
		string formatted = SourceFormatter.Format(source);
		Assert.Equal(source, formatted);
	}
}
