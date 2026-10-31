using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class TypeHeaderTests
{
	[Fact]
	public void Parses_generic_parameters_with_constraints_and_ctor_flag()
	{
		var result = Parser.Parse("type Box<T, K[IHashable], V[ctor()]> { }");

		Assert.Empty(result.Diagnostics);
		var box = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Equal(["T", "K", "V"], box.GenericParameters.Select(parameter => parameter.Name));
		Assert.Equal(3, box.GenericArity);

		var plain = box.GenericParameters[0];
		Assert.Empty(plain.Constraints);
		Assert.False(plain.HasCtorConstraint);

		var constrained = box.GenericParameters[1];
		Assert.Equal(["IHashable"], constrained.Constraints.Select(constraint => constraint.ToString()));
		Assert.False(constrained.HasCtorConstraint);

		var constructible = box.GenericParameters[2];
		Assert.Empty(constructible.Constraints);
		Assert.True(constructible.HasCtorConstraint);
	}

	[Fact]
	public void Parses_primary_constructor_and_parent_arguments()
	{
		var result = Parser.Parse("type B(i32 x)[A(x * 2), C, async_iter<i32>] { }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var parameter = Assert.Single(type.PrimaryParameters);
		Assert.Equal("x", parameter.Name);
		Assert.Equal("i32", parameter.Type?.ToString());

		Assert.Equal(3, type.Parents.Count);
		var first = type.Parents[0];
		Assert.Equal("A", first.Type.ToString());
		Assert.True(first.HasArgumentList);
		var argument = Assert.IsType<BinaryExpression>(Assert.Single(first.ConstructorArguments));
		Assert.Equal("*", argument.Operator);

		Assert.Equal("C", type.Parents[1].Type.ToString());
		Assert.False(type.Parents[1].HasArgumentList);
		Assert.Empty(type.Parents[1].ConstructorArguments);

		Assert.Equal("async_iter<i32>", type.Parents[2].Type.ToString());
		Assert.False(type.Parents[2].HasArgumentList);
	}

	[Fact]
	public void Parses_empty_primary_constructor_and_multiple_parents()
	{
		var result = Parser.Parse("type C()[A, B(x)] { }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Empty(type.PrimaryParameters);
		Assert.Equal(["A", "B"], type.Parents.Select(parent => parent.Type.ToString()));
		Assert.False(type.Parents[0].HasArgumentList);
		Assert.True(type.Parents[1].HasArgumentList);
	}

	[Fact]
	public void Parses_full_unit_header()
	{
		var result = Parser.Parse("unit! Name<T>(T value) { }");

		Assert.Empty(result.Diagnostics);
		var unit = Assert.IsType<UnitDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.True(unit.IsImmutable);
		Assert.Equal(["T"], unit.DeclaredGenericParameters.Select(parameter => parameter.Name));
		Assert.Equal("T", Assert.Single(unit.PrimaryParameters).Type?.ToString());
	}

	[Fact]
	public void Rejects_parents_on_unit()
	{
		var result = Parser.Parse("unit Name[T](T value)[Base] { }");

		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PAR013");
		var unit = Assert.IsType<UnitDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Equal("Name", unit.Name);
	}

	[Fact]
	public void Parses_generic_function_constraint_list()
	{
		var result = Parser.Parse("fun largest<T[num]>(T left, T right) -> T left");

		Assert.Empty(result.Diagnostics);
		var function = Assert.IsType<FunctionDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Equal(1, function.GenericArity);
		var genericParameter = Assert.Single(function.GenericParameters);
		Assert.Equal("T", genericParameter.Name);
		Assert.Equal("num", Assert.Single(genericParameter.Constraints).ToString());
		Assert.All(function.Parameters, parameter => Assert.Equal("T", parameter.Type?.ToString()));
		Assert.Equal("T", function.ReturnType?.ToString());
	}

	[Fact]
	public void Keeps_generic_parameters_on_static_and_abstract_members()
	{
		var result = Parser.Parse("@type Store { #fun make<T>(T seed) -> T seed @fun load<T[IHashable]>(T key) -> T }");

		Assert.Empty(result.Diagnostics);
		var store = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var make = Assert.IsType<FunctionDeclaration>(store.DeclaredMembers[0]);
		Assert.True(make.IsStatic);
		Assert.Equal(["T"], make.GenericParameters.Select(parameter => parameter.Name));

		var load = Assert.IsType<FunctionDeclaration>(store.DeclaredMembers[1]);
		Assert.True(load.IsAbstract);
		Assert.Equal("IHashable", Assert.Single(Assert.Single(load.GenericParameters).Constraints).ToString());
	}

	[Fact]
	public void Reports_missing_header_pieces()
	{
		var emptyGenerics = Parser.Parse("type A<> { }");
		var unclosedGenerics = Parser.Parse("type A<T { }");
		var missingParentType = Parser.Parse("type A[?] { }");
		var emptyConstraint = Parser.Parse("type A<T[]> { }");

		Assert.Contains(emptyGenerics.Diagnostics, diagnostic => diagnostic.Code == "PAR012");
		Assert.Contains(unclosedGenerics.Diagnostics, diagnostic => diagnostic.Code == "PAR012");
		Assert.Contains(missingParentType.Diagnostics, diagnostic => diagnostic.Code == "PAR011");
		Assert.Contains(emptyConstraint.Diagnostics, diagnostic => diagnostic.Code == "PAR012");
	}
}
