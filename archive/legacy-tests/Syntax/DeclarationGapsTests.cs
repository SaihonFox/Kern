using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class DeclarationGapsTests
{
	[Fact]
	public void Parses_auto_init_parameters_in_primary_constructors()
	{
		var typeResult = Parser.Parse("type Person(str $name, i32 age) { }");
		var unitResult = Parser.Parse("unit Point(f64 $x, f64 y) { }");

		Assert.Empty(typeResult.Diagnostics);
		var person = Assert.IsType<TypeDeclaration>(Assert.Single(typeResult.Tree.Declarations));
		Assert.True(person.PrimaryParameters[0].IsAutoInit);
		Assert.Equal("name", person.PrimaryParameters[0].Name);
		Assert.Equal("str", person.PrimaryParameters[0].Type?.ToString());
		Assert.False(person.PrimaryParameters[1].IsAutoInit);

		Assert.Empty(unitResult.Diagnostics);
		var point = Assert.IsType<UnitDeclaration>(Assert.Single(unitResult.Tree.Declarations));
		Assert.True(point.PrimaryParameters[0].IsAutoInit);
		Assert.Equal("x", point.PrimaryParameters[0].Name);
		Assert.False(point.PrimaryParameters[1].IsAutoInit);
	}

	[Fact]
	public void Parses_auto_init_parameter_in_constructor_member()
	{
		var result = Parser.Parse("type Person { ctor(str $name, i32 age) }");

		Assert.Empty(result.Diagnostics);
		var person = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var ctor = Assert.IsType<ConstructorMemberNode>(Assert.Single(person.DeclaredMembers));
		Assert.True(ctor.Parameters[0].IsAutoInit);
		Assert.Equal("name", ctor.Parameters[0].Name);
		Assert.False(ctor.Parameters[1].IsAutoInit);
	}

	[Fact]
	public void Parses_single_assignment_local_declarations()
	{
		var result = Parser.Parse("fun f { var! value = 10\ni32! num\ni32!? maybe = nil }");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		var declarations = function.Body.Statements.Cast<LocalDeclarationStatement>().ToArray();
		Assert.Equal(3, declarations.Length);

		Assert.True(declarations[0].IsSingleAssignment);
		Assert.Null(declarations[0].Type);
		Assert.Equal("value", declarations[0].Name);

		Assert.True(declarations[1].IsSingleAssignment);
		Assert.Equal("i32!", declarations[1].Type?.ToString());
		Assert.Null(declarations[1].Initializer);

		Assert.True(declarations[2].IsSingleAssignment);
		Assert.Equal("i32!?", declarations[2].Type?.ToString());
	}

	[Fact]
	public void Keeps_global_field_marker_and_required_flag()
	{
		var result = Parser.Parse("i32! total = 5\nvar! limit = 5");

		Assert.Empty(result.Diagnostics);
		var fields = result.Tree.Declarations.Cast<GlobalFieldDeclaration>().ToArray();
		Assert.Equal("i32!", fields[0].Type.ToString());
		Assert.True(fields[0].IsRequired);
		Assert.Equal("var", fields[1].Type.ToString());
		Assert.True(fields[1].IsRequired);
		Assert.True(fields[1].IsInferred);
	}

	[Fact]
	public void Reports_duplicate_type_markers()
	{
		var twiceNullable = Parser.Parse("fun f(i32?? value) { }");
		var twiceRequired = Parser.Parse("fun f(i32!! value) { }");
		var nullableThenRequired = Parser.Parse("fun f(i32!? value) { }");
		var requiredThenNullable = Parser.Parse("fun f(i32?! value) { }");

		Assert.Contains(twiceNullable.Diagnostics, diagnostic => diagnostic.Code == "PAR014");
		Assert.Contains(twiceRequired.Diagnostics, diagnostic => diagnostic.Code == "PAR014");
		Assert.Empty(nullableThenRequired.Diagnostics);
		Assert.Empty(requiredThenNullable.Diagnostics);
	}

	[Fact]
	public void Reports_invalid_tuple_types()
	{
		var empty = Parser.Parse("fun f(()) { }");
		var unnamedSingle = Parser.Parse("fun f((i32) value) { }");
		var namedSingle = Parser.Parse("fun f((i32 x) value) { }");

		Assert.Contains(empty.Diagnostics, diagnostic => diagnostic.Code == "PAR015");
		Assert.Contains(unnamedSingle.Diagnostics, diagnostic => diagnostic.Code == "PAR015");
		Assert.DoesNotContain(namedSingle.Diagnostics, diagnostic => diagnostic.Code == "PAR015");
	}

	[Fact]
	public void Rejects_union_types_outside_alias_position()
	{
		var alias = Parser.Parse("type id_value = [i32, str]");
		var parameter = Parser.Parse("fun f([i32, str] value) { }");

		Assert.Empty(alias.Diagnostics);
		Assert.Contains(parameter.Diagnostics, diagnostic => diagnostic.Code == "PAR016");
	}

	[Fact]
	public void Generic_call_type_arguments_are_full_type_syntax()
	{
		var function = Parser.ParseExpression("f<dict<str, i32>?>(x)");
		var structured = Parser.ParseExpression("apply<list<i32>>(value: x)");

		Assert.Empty(function.Diagnostics);
		var call = Assert.IsType<GenericCallExpression>(function.Expression);
		var typeArgument = Assert.IsType<NamedTypeSyntax>(Assert.Single(call.TypeArguments));
		Assert.Equal("dict<str,i32>?", typeArgument.ToString());

		Assert.Empty(structured.Diagnostics);
		var structuredCall = Assert.IsType<StructuredCallExpression>(structured.Expression);
		Assert.IsType<NamedTypeSyntax>(Assert.Single(structuredCall.TypeArguments));
	}
}
