using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class SingleExpressionBodyTests
{
	[Fact]
	public void Parses_single_expression_bodies_without_braces()
	{
		var function = Parser.Parse("fun square(i32 n) -> i32 n * n");
		var conditional = Parser.ParseExpression("if ready println(\"ready\") else println(\"wait\")");
		var loop = Parser.ParseExpression("loop while_ready process()");

		Assert.Empty(function.Diagnostics);
		Assert.Empty(conditional.Diagnostics);
		Assert.Empty(loop.Diagnostics);
		Assert.IsType<BinaryExpression>(Assert.IsType<ExpressionStatement>(
			Assert.Single(Assert.IsType<FunctionDeclaration>(Assert.Single(function.Tree.Declarations)).Body.Statements)).Expression);
		Assert.IsType<IfExpression>(conditional.Expression);
		Assert.IsType<LoopExpression>(loop.Expression);
	}
}
