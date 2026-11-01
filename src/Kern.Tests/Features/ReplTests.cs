using Kern.Cli.Repl;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>M11: <c>kernc repl</c> — объявления, переменные и присваивания переживают строки.</summary>
public sealed class ReplTests
{
	[Fact]
	public void Declarations_and_state_are_replayed_before_each_expression()
	{
		var session = new ReplSession();
		var stdout = new StringWriter();
		var stderr = new StringWriter();

		Assert.True(session.Evaluate("fun twice(i32 n) -> i32 n * 2", stdout, stderr), stderr.ToString());
		Assert.True(session.Evaluate("twice(21)", stdout, stderr), stderr.ToString());
		Assert.True(session.Evaluate("var x = 5", stdout, stderr), stderr.ToString());
		Assert.True(session.Evaluate("x + 1", stdout, stderr), stderr.ToString());
		Assert.True(session.Evaluate("x = 7", stdout, stderr), stderr.ToString());
		Assert.True(session.Evaluate("x", stdout, stderr), stderr.ToString());

		string[] lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(["42", "6", "7"], lines.Select(line => line.Trim()));
	}

	[Fact]
	public void Invalid_line_reports_an_error_and_keeps_state()
	{
		var session = new ReplSession();
		var stdout = new StringWriter();
		var stderr = new StringWriter();

		Assert.True(session.Evaluate("var x = 1", stdout, stderr), stderr.ToString());
		Assert.False(session.Evaluate("x + ", stdout, stderr));
		Assert.False(string.IsNullOrWhiteSpace(stderr.ToString()));
		Assert.True(session.Evaluate("x", stdout, stderr), stderr.ToString());
		Assert.Equal("1", stdout.ToString().Trim());
	}
}
