using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Semantics.Declarations;

public sealed class MemberCollectionTests
{
	[Fact]
	public void Collects_fields_with_required_initializer_and_static_flags()
	{
		var user = DeclarationTestSupport.Type(
			"type User { !str name  i32 age = 18  #i32 count = 0 }",
			"User");

		var name = Assert.IsType<FieldSymbol>(user.Members.FindField("name"));
		var age = Assert.IsType<FieldSymbol>(user.Members.FindField("age"));
		var count = Assert.IsType<FieldSymbol>(user.Members.FindField("count"));

		Assert.True(name.IsRequired);
		Assert.False(name.IsStatic);
		Assert.NotNull(age.Initializer);
		Assert.False(age.IsRequired);
		Assert.True(count.IsStatic);
		Assert.NotNull(count.Initializer);
		Assert.False(count.IsConst);
	}

	[Fact]
	public void Auto_property_creates_a_backing_field_and_both_accessors()
	{
		var example = DeclarationTestSupport.Type("type Example { i32 val { get; set } }", "Example");
		var property = Assert.Single(example.Members.Properties);

		Assert.True(property.IsAuto);
		Assert.NotNull(property.Getter);
		Assert.NotNull(property.Setter);
		Assert.Null(property.Init);
		Assert.Equal(FunctionKind.Getter, property.Getter!.Kind);
		Assert.Equal(FunctionKind.Setter, property.Setter!.Kind);

		var backing = Assert.IsType<FieldSymbol>(property.BackingField);
		Assert.Equal("_val", backing.Name);
		Assert.True(backing.IsBackingField);
		Assert.Equal("i32", backing.Type.ToString());
		Assert.Equal(Accessibility.Private, backing.Accessibility);

		var value = Assert.Single(property.Setter.Parameters);
		Assert.Equal("value", value.Name);
		Assert.Equal("i32", value.Type.ToString());
	}

	[Fact]
	public void Expression_property_is_get_only_without_a_backing_field()
	{
		var example = DeclarationTestSupport.Type("type Example { i32 b => 5 }", "Example");
		var property = Assert.Single(example.Members.Properties);

		Assert.False(property.IsAuto);
		Assert.Null(property.BackingField);
		Assert.NotNull(property.Getter);
		Assert.NotNull(property.ExpressionBody);
		Assert.Null(property.Setter);
		Assert.Null(property.Init);
		Assert.Equal("i32", property.Getter!.ReturnType!.ToString());
	}

	[Fact]
	public void Captures_set_and_init_accessors_with_parameters()
	{
		var example = DeclarationTestSupport.Type(
			"type Holder { i32 x  i32 val { get x; set(value) x = value; init x = 7 } }",
			"Holder");

		var property = Assert.Single(example.Members.Properties);

		Assert.NotNull(property.Getter);
		Assert.NotNull(property.Setter);
		Assert.NotNull(property.Init);
		Assert.Equal(FunctionKind.Init, property.Init!.Kind);
		// init не объявляет параметров, но получает неявный `value`, как set.
		Assert.Equal("value", Assert.Single(property.Init.Parameters).Name);
		Assert.Equal("value", Assert.Single(property.Setter!.Parameters).Name);
		Assert.Empty(property.Getter!.Parameters);
	}

	[Fact]
	public void Keeps_property_initializer_on_property_and_backing_field()
	{
		var counter = DeclarationTestSupport.Type("type Counter { i32 val { get; set } = 5 }", "Counter");
		var property = Assert.Single(counter.Members.Properties);

		Assert.NotNull(property.Initializer);
		Assert.NotNull(property.BackingField!.Initializer);
		Assert.Equal("5", Assert.IsType<IntegerLiteralExpression>(property.BackingField.Initializer).Text);
	}

	[Fact]
	public void Static_field_initializer_synthesizes_static_init()
	{
		var counter = DeclarationTestSupport.Type(
			"type Counter { #i32 seed = 1  #fun make() -> Counter Counter() }",
			"Counter");

		Assert.NotNull(counter.Members.StaticInitializer);
		Assert.True(counter.Members.StaticInitializer!.IsStatic);
		Assert.Equal("<static-init>", counter.Members.StaticInitializer.Name);
		Assert.True(Assert.IsType<FieldSymbol>(counter.Members.FindField("seed")).IsStatic);
	}

	[Fact]
	public void No_static_init_without_static_initializers()
	{
		var counter = DeclarationTestSupport.Type("type Counter { #i32 seed  #fun make() -> Counter Counter() }", "Counter");

		Assert.Null(counter.Members.StaticInitializer);
	}

	[Fact]
	public void Groups_method_overloads_and_keeps_distinct_signatures()
	{
		var calc = DeclarationTestSupport.Type(
			"type Calc { fun add(i32 a, i32 b) -> i32 { a + b } fun add(str a) -> str { a } }",
			"Calc");

		var overloads = calc.Members.GetFunctions("add");

		Assert.Equal(["add(i32,i32)", "add(str)"], overloads.Select(function => function.Signature));
		Assert.All(overloads, function => Assert.Equal(FunctionKind.Method, function.Kind));
		Assert.Same(calc, overloads[0].Owner);
	}
}
