using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class MatchBlockArmTests
{
	[Fact]
	public void Parses_match_block_arm_with_local_generic_declaration()
	{
		var result = Parser.Parse("fun demo { match connect() { Ok(db) => { table<User> users = orm.table<User>(db) users.create() } Err(e) => println(e) } }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var match = Assert.IsType<MatchExpression>(Assert.IsType<ExpressionStatement>(Assert.Single(function.Body.Statements)).Expression);
		var arm = Assert.IsType<BlockExpression>(match.Arms[0].Body);
		Assert.Equal(2, arm.Block.Statements.Count);
		Assert.IsType<LocalDeclarationStatement>(arm.Block.Statements[0]);
	}
}
