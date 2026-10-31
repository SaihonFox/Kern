using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class LocalFunctionTests
{
	[Fact]
	public void Parses_nested_function_declaration_after_return()
	{
		var result = Parser.Parse("fun somefun -> i32 { i32 num = getnum() ret num fun getnum -> i32 random.randint(1, 100) }");

		Assert.Empty(result.Diagnostics);
		var outer = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Equal(3, outer.Body.Statements.Count);
		var local = Assert.IsType<LocalFunctionStatement>(outer.Body.Statements[2]);
		Assert.Equal("getnum", local.Function.Name);
		Assert.Equal("i32", local.Function.ReturnType?.ToString());
		Assert.IsType<CallExpression>(Assert.Single(local.Function.Body.Statements) is ExpressionStatement statement
			? statement.Expression
			: null);
	}
}
