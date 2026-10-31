using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Semantics.Declarations;

public sealed class ConstructorTests
{
	[Fact]
	public void Synthesizes_primary_constructor_from_type_parameters()
	{
		var user = DeclarationTestSupport.Type("type User(str name, i32 age) { }", "User");

		var constructor = user.PrimaryConstructor;
		Assert.NotNull(constructor);
		Assert.True(constructor!.IsPrimaryConstructor);
		Assert.Equal(FunctionKind.Constructor, constructor.Kind);
		Assert.Equal("ctor", constructor.Name);
		Assert.Equal(["str", "i32"], constructor.Parameters.Select(parameter => parameter.Type.ToString()));
		Assert.Equal("ctor(str,i32)", constructor.Signature);
		Assert.Same(constructor, Assert.Single(user.Members.Constructors));
		Assert.Equal(["name", "age"], user.Members.Fields.Select(field => field.Name));
		Assert.All(user.Members.Fields, field => Assert.Equal(Accessibility.Public, field.Accessibility));
	}

	[Fact]
	public void Auto_init_parameter_creates_a_field_and_links_it()
	{
		var user = DeclarationTestSupport.Type("type User(str $name) { }", "User");

		var parameter = Assert.Single(user.PrimaryConstructor!.Parameters);
		var field = Assert.IsType<FieldSymbol>(user.Members.FindField("name"));

		Assert.True(parameter.IsAutoInit);
		Assert.Same(field, parameter.Field);
		Assert.True(field.IsAutoInit);
		Assert.Equal("str", field.Type.ToString());
		Assert.False(field.IsRequired);
	}

	[Fact]
	public void Auto_init_prefers_an_already_declared_field()
	{
		var user = DeclarationTestSupport.Type(
			"type User { str name  ctor(str $name) }",
			"User");

		var constructor = Assert.Single(user.Members.Constructors);
		var parameter = Assert.Single(constructor.Parameters);

		Assert.Single(user.Members.Fields);
		Assert.Same(user.Members.FindField("name"), parameter.Field);
		Assert.False(user.Members.FindField("name")!.IsAutoInit);
	}

	[Fact]
	public void Keeps_explicit_constructor_chain()
	{
		var child = DeclarationTestSupport.Type(
			"type Base { } type Child(i32 x)[Base] { ctor(i32 y, bool flag) : $(y) { } }",
			"Child");

		Assert.Equal(2, child.Members.Constructors.Count);
		var explicitConstructor = Assert.Single(child.Members.Constructors, item => !item.IsPrimaryConstructor);

		Assert.NotNull(explicitConstructor.Chain);
		Assert.Single(explicitConstructor.Chain!.Arguments);
		Assert.Equal(Accessibility.Private, explicitConstructor.Accessibility);
		Assert.Same(child, explicitConstructor.Owner);
	}

	[Fact]
	public void Synthesizes_default_constructor_when_none_declared()
	{
		var empty = DeclarationTestSupport.Type("type Empty { }", "Empty");

		var constructor = Assert.Single(empty.Members.Constructors);

		Assert.False(constructor.IsPrimaryConstructor);
		Assert.Empty(constructor.Parameters);
		Assert.Equal("ctor()", constructor.Signature);
		Assert.Equal(Accessibility.Public, constructor.Accessibility);
		Assert.Same(empty, constructor.Owner);
	}

	[Fact]
	public void No_default_constructor_when_explicit_one_exists()
	{
		var empty = DeclarationTestSupport.Type("type Empty { ctor(str name) { } }", "Empty");

		Assert.Single(empty.Members.Constructors);
	}

	[Fact]
	public void Unit_primary_parameters_form_the_primary_constructor()
	{
		var point = DeclarationTestSupport.Type("unit Point(f64 x, f64 y)", "Point");

		var constructor = point.PrimaryConstructor;
		Assert.NotNull(constructor);
		Assert.Equal(["f64", "f64"], constructor!.Parameters.Select(parameter => parameter.Type.ToString()));
		Assert.True(point.IsValueType);
	}

	[Fact]
	public void Reports_duplicate_constructor_signature()
	{
		var diagnostics = DeclarationTestSupport.Diagnostics(
			"type Person { ctor(i32 age) { } ctor(i32 other) { } }");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN1400", diagnostic.Code);
		Assert.Equal("Повторная сигнатура конструктора", diagnostic.Message);
	}

	[Fact]
	public void Keeps_first_duplicate_constructor_only()
	{
		var module = DeclarationTestSupport.Collect(
			"type Person { ctor(i32 age) { } ctor(i32 other) { } }").Module;

		Assert.Single(module.FindType("Person")!.Members.Constructors);
	}
}
