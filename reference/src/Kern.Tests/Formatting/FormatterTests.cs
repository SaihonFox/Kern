using Kern.Compiler.Formatting;
using Xunit;

namespace Kern.Tests.Formatting;

public class FormatterTests
{
	private static string Fmt(string text)
	{
		var r = SourceFormatter.Format("t.kern", text);
		Assert.True(r.Text is not null, r.Error);
		return r.Text!.Replace("\r\n", "\n");   // по умолчанию форматтер выдаёт CRLF; для удобства сравниваем с LF
	}

	[Fact]
	public void FixesIndentationByBrackets()
	{
		string src = "fun main {\n  i32 a = 1\n      if a > 0 {\n println(a)\n   }\n}\n";
		Assert.Equal("fun main {\n\ti32 a = 1\n\tif a > 0 {\n\t\tprintln(a)\n\t}\n}\n", Fmt(src));
	}

	[Fact]
	public void TrimsTrailingSpacesAndCollapsesBlankLines()
	{
		string src = "fun main {   \n\n\n\n    println(1)   \n}\n\n\n";
		Assert.Equal("fun main {\n\n\tprintln(1)\n}\n", Fmt(src));
	}

	[Fact]
	public void KeepsComments()
	{
		string src = "// заголовок\nfun main {\n// внутри\nprintln(1) // хвост\n}\n";
		string result = Fmt(src);
		Assert.Contains("// заголовок", result);
		Assert.Contains("\n\t// внутри", result);
		Assert.Contains("\tprintln(1) // хвост", result);
	}

	[Fact]
	public void KeepsMultilineStringsAndBlockComments()
	{
		string src = "fun main {\n/* один\n   два */\nprintln(1)\n}\n";
		Assert.Contains("/* один\n   два */", Fmt(src));
	}

	[Fact]
	public void IsIdempotent()
	{
		string src = "fun main {\n\ti32 a = 1\n\tloop i in 0..3 {\n\t\tprintln(i)\n\t}\n}\n";
		string once = Fmt(src);
		Assert.Equal(once, Fmt(once));
	}

	[Fact]
	public void ContinuationLinesGetOneExtraLevel()
	{
		string src = "fun main {\nvar x = [1, 2, 3]\n.map(fun (i32 v) -> i32 v * 2)\nprintln(x)\n}\n";
		string result = Fmt(src);
		Assert.Contains("\n\tvar x = [1, 2, 3]\n\t\t.map(", result);
	}

	[Fact]
	public void RefusesCodeWithSyntaxErrors()
	{
		var r = SourceFormatter.Format("t.kern", "fun main {\n  println(\n");
		Assert.Null(r.Text);
		Assert.NotNull(r.Error);
	}

	[Fact]
	public void DefaultsToCrLf()
	{
		var r = SourceFormatter.Format("t.kern", "fun main {\nprintln(1)\n}\n");
		Assert.Equal("fun main {\r\n\tprintln(1)\r\n}\r\n", r.Text);
	}

	[Fact]
	public void SupportsSpacesAndLf()
	{
		var r = SourceFormatter.Format("t.kern", "fun main {\r\n\tprintln(1)\r\n}\r\n", new FormatOptions(false, "\n"));
		Assert.Equal("fun main {\n    println(1)\n}\n", r.Text);
	}

	[Fact]
	public void IfBodyOnNextLineIsIndentedOnce()
	{
		string result = Fmt("fun f(i32 x) -> i32 {\nif x > 0\nx\nelse\n0\n}\n");
		Assert.Contains("\n\tif x > 0\n\t\tx\n\telse\n\t\t0\n", result);
	}

	[Fact]
	public void AllExamplesAreStableUnderFormatting()
	{
		// форматирование не должно ломать ни один пример (токены сверяются внутри форматтера)
		foreach (var file in Directory.EnumerateFiles(Path.Combine(Harness.KernHarness.RepoRoot(), "examples"), "*.kern", SearchOption.AllDirectories))
		{
			var r = SourceFormatter.Format(file, File.ReadAllText(file));
			Assert.True(r.Text is not null, $"{file}: {r.Error}");
		}
	}
}
