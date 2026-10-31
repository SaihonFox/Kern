using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class RangePatternTests
{
	[Fact]
	public void Parses_inclusive_and_exclusive_literal_match_ranges()
	{
		var exclusive = Parser.ParseExpression("match n { 1..9 => \"small\"; _ => \"other\" }");
		var inclusive = Parser.ParseExpression("match n { 1..=9 => \"small\"; _ => \"other\" }");

		Assert.Empty(exclusive.Diagnostics);
		Assert.Empty(inclusive.Diagnostics);
		Assert.False(Assert.IsType<RangePattern>(Assert.IsType<MatchExpression>(exclusive.Expression).Arms[0].Pattern).IsInclusive);
		Assert.True(Assert.IsType<RangePattern>(Assert.IsType<MatchExpression>(inclusive.Expression).Arms[0].Pattern).IsInclusive);
	}

	[Fact]
	public void Reports_malformed_range_pattern_and_recovers_to_later_arm()
	{
		var result = Parser.ParseExpression("match n { 1.. => \"bad\"; _ => \"fallback\" }");

		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PAR006");
		var match = Assert.IsType<MatchExpression>(result.Expression);
		Assert.True(match.Arms.Count >= 2);
		Assert.IsType<WildcardPattern>(match.Arms[^1].Pattern);
	}

	[Fact]
	public void Parses_reserved_project_keyword_as_member_name()
	{
		var result = Parser.ParseExpression("transpiler(\"kotlin\").project(\"sample\", \"out/kotlin\")");

		Assert.Empty(result.Diagnostics);
		var call = Assert.IsType<CallExpression>(result.Expression);
		Assert.Equal("project", Assert.IsType<MemberAccessExpression>(call.Target).Member);
	}
}
