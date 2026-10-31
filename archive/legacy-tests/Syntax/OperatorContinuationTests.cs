using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class OperatorContinuationTests
{
	[Fact]
	public void Parses_operator_member_with_owner_and_return_type()
	{
		var result = Parser.Parse("type Vec { op +(Vec other) -> Vec { ret other } }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var op = Assert.IsType<OperatorMemberNode>(Assert.Single(type.DeclaredMembers));
		Assert.Equal("+", op.Operator);
		Assert.Equal("Vec", op.ReturnType?.ToString());
		Assert.Single(op.Parameters);
	}

	[Fact]
	public void Parses_qualified_operator_member()
	{
		var result = Parser.Parse("type Vec { op i32.*(Vec other) -> Vec { ret other } }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var op = Assert.IsType<OperatorMemberNode>(Assert.Single(type.DeclaredMembers));
		Assert.Equal("i32.*", op.Operator);
	}

	[Fact]
	public void Parses_leading_dot_member_chain_across_lines()
	{
		var result = Parser.ParseExpression("items\n\t.map(transform)\n\t.filter(ready)");

		Assert.Empty(result.Diagnostics);
		var filter = Assert.IsType<CallExpression>(result.Expression);
		var filterMember = Assert.IsType<MemberAccessExpression>(filter.Target);
		Assert.Equal("filter", filterMember.Member);
		var mapCall = Assert.IsType<CallExpression>(filterMember.Target);
		Assert.Equal("map", Assert.IsType<MemberAccessExpression>(mapCall.Target).Member);
	}

	[Fact]
	public void Parses_unary_and_elvis_operators()
	{
		var result = Parser.ParseExpression("!ready ?: fallback");

		Assert.Empty(result.Diagnostics);
		var elvis = Assert.IsType<BinaryExpression>(result.Expression);
		Assert.Equal("?:", elvis.Operator);
		Assert.IsType<UnaryExpression>(elvis.Left);
	}
}
