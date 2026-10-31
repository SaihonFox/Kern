using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class LoopExpressionTests
{
	[Fact]
	public void Parses_typed_var_existing_and_tuple_iteration_bindings()
	{
		var typed = Parse("loop i32 i in 1..5 i");
		var inferred = Parse("loop var i in values i");
		var existing = Parse("loop i in values i");
		var tuple = Parse("loop (str key, i32 value) in entries value");

		Assert.Equal("i32", Assert.Single(typed.Bindings).Type?.ToString());
		Assert.True(Assert.Single(inferred.Bindings).IsMutable);
		Assert.Equal("i", Assert.Single(existing.Bindings).Name);
		Assert.Equal(2, tuple.Bindings.Count);
		Assert.Equal("key", tuple.Bindings[0].Name);
	}

	[Fact]
	public void Parses_omitted_binding_range_and_collection_loops()
	{
		var range = Parse("loop 0..3 work()");
		var collection = Parse("loop var value in values work()");

		Assert.Equal(LoopExpressionKind.Iteration, range.Kind);
		Assert.IsType<RangeExpression>(range.Iterable);
		Assert.Empty(range.Bindings);
		Assert.Equal("values", Assert.IsType<IdentifierExpression>(collection.Iterable).Name);
	}

	[Fact]
	public void Parses_condition_with_parenthesized_membership_and_body()
	{
		var loop = Parse("loop (k in allowed) k += 1");

		Assert.Equal(LoopExpressionKind.Condition, loop.Kind);
		Assert.IsType<BinaryExpression>(loop.Condition);
		var assignment = Assert.IsType<AssignmentStatement>(loop.Body);
		Assert.Equal("+=", assignment.Operator);
	}

	[Fact]
	public void Parses_infinite_and_do_while_block_loops()
	{
		var infinite = Parse("loop { stop }");
		var doWhile = Parse("loop { next }: ready");

		Assert.Equal(LoopExpressionKind.Infinite, infinite.Kind);
		Assert.Equal(LoopExpressionKind.DoWhile, doWhile.Kind);
		Assert.Equal("ready", Assert.IsType<IdentifierExpression>(doWhile.Condition).Name);
	}

	[Fact]
	public void Parses_async_loop_with_and_without_binding()
	{
		var bound = Parse("async loop var item in stream item");
		var unbound = Parse("async loop stream.next() await stream.next()");

		Assert.True(bound.IsAsync);
		Assert.Equal("item", Assert.Single(bound.Bindings).Name);
		Assert.True(unbound.IsAsync);
		Assert.Empty(unbound.Bindings);
		Assert.IsType<MemberAccessExpression>(Assert.IsType<CallExpression>(unbound.Iterable).Target);
	}

	[Fact]
	public void Leaves_non_loop_expression_parsing_unchanged()
	{
		var parsed = Parser.ParseExpression("value + 1");

		Assert.Empty(parsed.Diagnostics);
		Assert.IsType<BinaryExpression>(parsed.Expression);
	}

	private static LoopExpression Parse(string source)
	{
		var result = Parser.ParseExpression(source);
		Assert.Empty(result.Diagnostics);
		return Assert.IsType<LoopExpression>(result.Expression);
	}
}
