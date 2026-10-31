using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class ParserRecoveryTests
{
	[Fact]
	public void Recovers_and_reports_multiple_syntax_errors()
	{
		const string source = "fun broken(a { 1 + ; } fun also { ) }";
		var result = Parser.Parse(source);

		Assert.True(result.Diagnostics.Count >= 2);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code.StartsWith("PAR", StringComparison.Ordinal));
		Assert.All(result.Diagnostics, diagnostic =>
		{
			Assert.InRange(diagnostic.Span.Start, 0, source.Length);
			Assert.InRange(diagnostic.Span.Length, 0, source.Length - diagnostic.Span.Start);
		});
	}
}
