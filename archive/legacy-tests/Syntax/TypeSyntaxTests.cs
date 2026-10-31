using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class TypeSyntaxTests
{
	[Fact]
	public void Parses_nested_named_generics_and_normalizes_markers()
	{
		var nested = Assert.IsType<NamedTypeSyntax>(ParseParameterType("list<dict<str, i32>>"));
		Assert.Equal("list<dict<str,i32>>", nested.ToString());
		Assert.Equal("dict<str,i32>", Assert.Single(nested.Arguments).ToString());
		Assert.False(nested.IsNullable);
		Assert.False(nested.IsSingleAssignment);

		Assert.Equal("i32!?", ParseParameterType("i32!?").ToString());
		var reordered = Assert.IsType<NamedTypeSyntax>(ParseParameterType("i32?!"));
		Assert.Equal("i32!?", reordered.ToString());
		Assert.True(reordered.IsNullable);
		Assert.True(reordered.IsSingleAssignment);
	}

	[Fact]
	public void Parses_function_types_with_nested_functions()
	{
		Assert.Equal("fun(i32,str)->bool", ParseParameterType("fun(i32, str) -> bool").ToString());
		Assert.Equal("fun()->void", ParseParameterType("fun -> void").ToString());

		var nested = Assert.IsType<FunctionTypeSyntax>(
			ParseParameterType("fun(fun(i32) -> i32) -> i32"));
		Assert.Equal("fun(fun(i32)->i32)->i32", nested.ToString());
		var inner = Assert.IsType<FunctionTypeSyntax>(Assert.Single(nested.Parameters));
		Assert.Equal("i32", Assert.Single(inner.Parameters).ToString());
		Assert.Equal("i32", inner.ReturnType?.ToString());
		Assert.Equal("i32", nested.ReturnType?.ToString());
	}

	[Fact]
	public void Parses_tuple_types_with_and_without_element_names()
	{
		Assert.Equal("(i32,str)", ParseParameterType("(i32, str)").ToString());
		Assert.Equal("(i32 x,f64 y)", ParseParameterType("(i32 x, f64 y)").ToString());

		var named = Assert.IsType<TupleTypeSyntax>(ParseParameterType("(i32 x, f64 y)"));
		Assert.Equal(["x", "y"], named.Elements.Select(element => element.Name));
		Assert.Equal(["i32", "f64"], named.Elements.Select(element => element.Type.ToString()));
	}

	[Fact]
	public void Parses_tuple_type_as_function_return_type()
	{
		var result = Parser.Parse("fun pair() -> (i32 low, str high) { 1 }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var tuple = Assert.IsType<TupleTypeSyntax>(function.ReturnType);
		Assert.Equal("(i32 low,str high)", tuple.ToString());
	}

	[Fact]
	public void Parses_union_alias_variants_with_generic_arguments()
	{
		var result = Parser.Parse("type id_value = [list<i32>, str?]");

		Assert.Empty(result.Diagnostics);
		var alias = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Equal(["list<i32>", "str?"],
			alias.DeclaredUnionVariants.Select(variant => variant.ToString()));
	}

	[Fact]
	public void Reports_missing_generic_argument_and_empty_union()
	{
		var missingArgument = Parser.Parse("fun f(list<> value) { }");
		var emptyUnion = Parser.Parse("type id_value = []");

		Assert.Contains(missingArgument.Diagnostics, diagnostic => diagnostic.Code == "PAR003");
		Assert.Contains(emptyUnion.Diagnostics, diagnostic => diagnostic.Code == "PAR003");
	}

	[Fact]
	public void Parses_nested_generic_closers_as_separate_angles()
	{
		var pair = Assert.IsType<NamedTypeSyntax>(ParseParameterType("list<list<i32>>"));
		Assert.Equal("list", pair.Name);
		var inner = Assert.IsType<NamedTypeSyntax>(Assert.Single(pair.Arguments));
		Assert.Equal("list", inner.Name);
		Assert.Equal("i32", Assert.Single(inner.Arguments).ToString());

		var triple = Assert.IsType<NamedTypeSyntax>(ParseParameterType("list<list<list<i32>>>"));
		var middle = Assert.IsType<NamedTypeSyntax>(Assert.Single(triple.Arguments));
		var deepest = Assert.IsType<NamedTypeSyntax>(Assert.Single(middle.Arguments));
		Assert.Equal("i32", Assert.Single(deepest.Arguments).ToString());

		var dict = Assert.IsType<NamedTypeSyntax>(ParseParameterType("dict<str, list<list<i32>>>"));
		Assert.Equal(2, dict.Arguments.Count);
		Assert.Equal("str", dict.Arguments[0].ToString());
		Assert.Equal("list<list<i32>>", dict.Arguments[1].ToString());
	}

	[Fact]
	public void Parses_local_declaration_with_double_closer()
	{
		// Lookahead объявления локальной переменной обязан считать `>>` за два закрывающих угла.
		var result = Parser.Parse("""
			fun f() {
				list<list<i32>> x
			}
			""");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var local = Assert.IsType<LocalDeclarationStatement>(Assert.Single(function.Body.Statements));
		Assert.Equal("list<list<i32>>", local.Type?.ToString());
		Assert.Equal("x", local.Name);
	}

	[Fact]
	public void Shift_expression_is_not_split_into_type_closers()
	{
		var result = Parser.ParseExpression("a >> b");

		Assert.Empty(result.Diagnostics);
		var shift = Assert.IsType<BinaryExpression>(result.Expression);
		Assert.Equal(">>", shift.Operator);
		Assert.Equal("a", Assert.IsType<IdentifierExpression>(shift.Left).Name);
		Assert.Equal("b", Assert.IsType<IdentifierExpression>(shift.Right).Name);
	}

	private static TypeSyntax ParseParameterType(string type)
	{
		var result = Parser.Parse($"fun probe({type} value) {{ }}");
		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		return Assert.Single(function.Parameters).Type!;
	}
}
