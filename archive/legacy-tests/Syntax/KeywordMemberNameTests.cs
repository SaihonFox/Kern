using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class KeywordMemberNameTests
{
	[Theory]
	[InlineData("host.use(middleware.logger())", "use")]
	[InlineData("host.stop()", "stop")]
	public void Parses_reserved_keywords_as_member_names(string source, string expectedMember)
	{
		var result = Parser.ParseExpression(source);

		Assert.Empty(result.Diagnostics);
		var call = Assert.IsType<CallExpression>(result.Expression);
		Assert.Equal(expectedMember, Assert.IsType<MemberAccessExpression>(call.Target).Member);
	}

	[Fact]
	public void Parses_lambda_argument_to_reserved_use_member()
	{
		var result = Parser.ParseExpression("host.use(fun(ctx c) -> response c.send())");

		Assert.Empty(result.Diagnostics);
		var call = Assert.IsType<CallExpression>(result.Expression);
		Assert.Equal("use", Assert.IsType<MemberAccessExpression>(call.Target).Member);
		Assert.IsType<LambdaExpression>(Assert.Single(call.Arguments));
	}
}
