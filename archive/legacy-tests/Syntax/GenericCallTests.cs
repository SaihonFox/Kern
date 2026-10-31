using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class GenericCallTests
{
	[Fact]
	public void Parses_explicit_generic_function_call()
	{
		var result = Parser.ParseExpression("f<i32>(x)");

		Assert.Empty(result.Diagnostics);
		var call = Assert.IsType<GenericCallExpression>(result.Expression);
		Assert.Equal("f", Assert.IsType<IdentifierExpression>(call.Target).Name);
		Assert.Equal(["i32"], call.TypeArguments.Select(type => type.ToString()));
		Assert.Equal("x", Assert.IsType<IdentifierExpression>(Assert.Single(call.Arguments)).Name);
	}

	[Fact]
	public void Parses_nested_multiple_generic_arguments()
	{
		var result = Parser.ParseExpression("f<list<i32>, str>(x)");

		Assert.Empty(result.Diagnostics);
		var call = Assert.IsType<GenericCallExpression>(result.Expression);
		Assert.Equal(["list<i32>", "str"], call.TypeArguments.Select(type => type.ToString()));
	}

	[Fact]
	public void Parses_generic_construction_with_empty_arguments()
	{
		var result = Parser.ParseExpression("Stack<i32>()");

		Assert.Empty(result.Diagnostics);
		var call = Assert.IsType<GenericCallExpression>(result.Expression);
		Assert.Equal(["i32"], call.TypeArguments.Select(type => type.ToString()));
		Assert.Empty(call.Arguments);
	}

	[Fact]
	public void Preserves_comparison_typed_collection_and_inferred_call_parsing()
	{
		var comparison = Parser.ParseExpression("a < b");
		var collection = Parser.ParseExpression("<i32>[]");
		var ordinaryCall = Parser.ParseExpression("f(x)");

		Assert.Empty(comparison.Diagnostics);
		Assert.IsType<BinaryExpression>(comparison.Expression);
		Assert.Empty(collection.Diagnostics);
		Assert.IsType<TypedCollectionExpression>(collection.Expression);
		Assert.Empty(ordinaryCall.Diagnostics);
		Assert.IsType<CallExpression>(ordinaryCall.Expression);
	}
}
