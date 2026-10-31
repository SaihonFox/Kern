using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Xunit;

namespace Kern.Tests.Semantics.Declarations;

public sealed class GenericAndInheritanceTests
{
	[Fact]
	public void Resolves_class_generic_parameters_and_constraints()
	{
		var box = DeclarationTestSupport.Type("type Box<T[num]> { T value }", "Box");

		var parameter = Assert.Single(box.GenericParameters);
		Assert.Equal("T", parameter.Name);
		Assert.Equal([PrimitiveType.Num], parameter.Constraints);
		Assert.Equal(["T"], box.GenericParameterNames);
		Assert.Equal(1, box.GenericArity);
	}

	[Fact]
	public void Members_use_the_class_scope_and_constructed_types_share_the_symbol()
	{
		var module = DeclarationTestSupport.Module(
			"type Box<T> { T value  fun get -> T value } type Holder { Box<i32> box }");

		var box = module.FindType("Box")!;
		var holderField = Assert.IsType<FieldSymbol>(module.FindType("Holder")!.Members.FindField("box"));
		var boxType = Assert.IsType<ClassType>(holderField.Type);

		Assert.Same(box, boxType.Symbol);
		Assert.Equal("i32", Assert.Single(boxType.Arguments).ToString());
		Assert.Equal("T", Assert.IsType<FieldSymbol>(box.Members.FindField("value")).Type.ToString());
		Assert.Equal("T", Assert.Single(box.Members.GetFunctions("get")).ReturnType!.ToString());
	}

	[Fact]
	public void Generic_method_scope_sits_on_top_of_the_class_scope()
	{
		var box = DeclarationTestSupport.Type(
			"type Box<T> { fun convert<U>(U value, T other) -> U value }",
			"Box");

		var convert = Assert.Single(box.Members.GetFunctions("convert"));

		Assert.Equal(1, convert.GenericArity);
		Assert.Equal("U", Assert.Single(convert.GenericParameters).Name);
		Assert.Equal(["U", "T"], convert.Parameters.Select(parameter => parameter.Type.ToString()));
		Assert.Equal("U", convert.ReturnType!.ToString());
		Assert.Equal("convert<1>(!0,T)", convert.Signature);
	}

	[Fact]
	public void Method_constraints_see_class_parameters()
	{
		var box = DeclarationTestSupport.Type(
			"type Box<T> { fun same<U[T]>(U value) -> U value }",
			"Box");

		var classParameter = Assert.Single(box.GenericParameters);
		var methodParameter = Assert.Single(Assert.Single(box.Members.GetFunctions("same")).GenericParameters);

		Assert.Equal(classParameter, Assert.Single(methodParameter.Constraints));
	}

	[Fact]
	public void Collects_direct_bases_with_class_generic_arguments()
	{
		var child = DeclarationTestSupport.Type(
			"type Root { } type Parent<T> { } type Child<T>[Parent<T>, Root] { }",
			"Child");

		Assert.Equal(["Parent<T>", "Root"], child.Bases.Select(type => type.ToString()));
		Assert.IsType<ClassType>(child.Bases[0]);
	}

	[Fact]
	public void Abstract_methods_are_abstract_and_open_without_bodies()
	{
		var baseType = DeclarationTestSupport.Type("@type Base { @fun calculate() -> f64 }", "Base");

		var method = Assert.Single(baseType.Members.GetFunctions("calculate"));

		Assert.True(method.IsAbstract);
		Assert.True(method.IsOpen);
		Assert.Equal("f64", method.ReturnType!.ToString());
	}

	[Fact]
	public void Reports_duplicate_type_generic_parameter_name()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics("type Box<T, T> { }");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN1408", diagnostic.Code);
		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
		Assert.Equal("Повторный generic-параметр 'T' у типа 'Box'", diagnostic.Message);
	}

	[Fact]
	public void Reports_duplicate_function_generic_parameter_name()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics("fun pick<T, T>(i32 value) {}");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN1408", diagnostic.Code);
		Assert.Equal("Повторный generic-параметр 'T' у функции 'pick'", diagnostic.Message);
	}

	[Fact]
	public void Shadowing_a_type_name_is_a_warning()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics("type User { } type Box<User> { }");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN1409", diagnostic.Code);
		Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
		Assert.False(diagnostic.IsError);
		Assert.Equal("Generic-параметр 'User' скрывает тип с тем же именем", diagnostic.Message);
	}

	[Fact]
	public void Primitive_names_are_also_shadowing_warnings()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics("type Box<i32> { }");

		Assert.Equal("KRN1409", Assert.Single(diagnostics).Code);
	}
}
