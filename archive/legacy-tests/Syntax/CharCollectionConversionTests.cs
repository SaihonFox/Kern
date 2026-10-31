using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class CharCollectionConversionTests
{
	[Fact]
	public void Parses_contextual_char_collection_conversion_forms()
	{
		var result = Parser.Parse("fun main { list<char> c = \"text\" str s = <char>['h', 'i'] }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var declarations = function.Body.Statements.Cast<LocalDeclarationStatement>().ToArray();

		Assert.Equal("list<char>", declarations[0].Type?.ToString());
		Assert.IsType<StringLiteralExpression>(declarations[0].Initializer);
		Assert.Equal("str", declarations[1].Type?.ToString());
		var chars = Assert.IsType<TypedCollectionExpression>(declarations[1].Initializer);
		Assert.Equal("char", chars.ElementType?.ToString());
		Assert.Equal(["'h'", "'i'"], chars.Items.Cast<CharLiteralExpression>().Select(item => item.Text));
	}
}
