using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class StructuredCallTests
{
	[Fact]
	public void Parses_named_arguments_as_labeled_call_arguments()
	{
		var result = Parser.ParseExpression("greet(age: 25, name: \"Bob\")");

		Assert.Empty(result.Diagnostics);
		var call = Assert.IsType<StructuredCallExpression>(result.Expression);
		Assert.Equal("greet", Assert.IsType<IdentifierExpression>(call.Target).Name);
		Assert.Equal(["age", "name"], call.Arguments.Select(argument => argument.Name));
		Assert.IsType<IntegerLiteralExpression>(call.Arguments[0].Value);
		Assert.IsType<StringLiteralExpression>(call.Arguments[1].Value);
	}

	[Fact]
	public void Parses_nested_calls_with_named_arguments()
	{
		var result = Parser.ParseExpression("outer(inner(value: 1), option: make(name: \"x\"))");

		Assert.Empty(result.Diagnostics);
		var outer = Assert.IsType<StructuredCallExpression>(result.Expression);
		var inner = Assert.IsType<StructuredCallExpression>(outer.Arguments[0].Value);
		Assert.Equal("value", Assert.Single(inner.Arguments).Name);
		Assert.Equal("option", outer.Arguments[1].Name);
		Assert.IsType<StructuredCallExpression>(outer.Arguments[1].Value);
	}

	[Fact]
	public void Parses_character_literal_as_its_own_expression_kind()
	{
		var result = Parser.ParseExpression("'\\u4564'");

		Assert.Empty(result.Diagnostics);
		var literal = Assert.IsType<CharLiteralExpression>(result.Expression);
		Assert.Equal("'\\u4564'", literal.Text);
	}

	[Fact]
	public void Parses_lambda_as_an_explicit_call_argument()
	{
		var result = Parser.ParseExpression("w.target(\"app\", fun (target t) { t.kind = \"app\" })");

		Assert.Empty(result.Diagnostics);
		var call = Assert.IsType<CallExpression>(result.Expression);
		Assert.Equal(2, call.Arguments.Count);
		var lambda = Assert.IsType<LambdaExpression>(call.Arguments[1]);
		Assert.Equal("\"app\"", Assert.IsType<StringLiteralExpression>(call.Arguments[0]).Text);
		Assert.Equal("target", Assert.Single(lambda.Parameters).Type?.ToString());
		Assert.IsType<BlockStatement>(lambda.Body);
	}

	[Fact]
	public void Requires_lambda_arguments_inside_parentheses()
	{
		var call = Parser.ParseExpression("apply(5, fun(i32 n) -> i32 n + 1)");
		var trailing = Parser.ParseExpression("apply(5) fun(i32 n) -> i32 n + 1");

		Assert.Empty(call.Diagnostics);
		var parsedCall = Assert.IsType<CallExpression>(call.Expression);
		Assert.IsType<LambdaExpression>(parsedCall.Arguments[1]);
		Assert.NotEmpty(trailing.Diagnostics);
	}

	[Fact]
	public void Preserves_generic_call_type_arguments_and_ordinary_positional_calls()
	{
		var generic = Parser.ParseExpression("apply<list<i32>>(value: make(), handler: fun(i32 n) -> i32 n)");
		var ordinary = Parser.ParseExpression("apply(5)");

		Assert.Empty(generic.Diagnostics);
		var structured = Assert.IsType<StructuredCallExpression>(generic.Expression);
		Assert.Equal(["list<i32>"], structured.TypeArguments.Select(type => type.ToString()));
		Assert.Equal(["value", "handler"], structured.Arguments.Select(argument => argument.Name));
		Assert.IsType<LambdaExpression>(structured.Arguments[1].Value);
		Assert.Empty(ordinary.Diagnostics);
		Assert.IsType<CallExpression>(ordinary.Expression);
	}

	[Fact]
	public void Reports_positional_argument_after_named_argument()
	{
		var result = Parser.ParseExpression("call(named: 1, 2)");

		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PAR009");
	}

	[Fact]
	public void Reports_trailing_comma_in_generic_type_arguments()
	{
		var result = Parser.ParseExpression("call<i32,>()");

		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PAR003");
	}
}

