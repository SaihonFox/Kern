using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Syntax;

public sealed class MemberDeclarationTests
{
	[Fact]
	public void Uses_public_accessibility_for_top_level_declarations()
	{
		var result = Parser.Parse("type A { }\nunit U\nfun run { }\ni32 total = 0");

		Assert.Empty(result.Diagnostics);
		Assert.Equal(Accessibility.Public, Assert.IsType<TypeDeclaration>(result.Tree.Declarations[0]).Accessibility);
		Assert.Equal(Accessibility.Public, Assert.IsType<UnitDeclaration>(result.Tree.Declarations[1]).Accessibility);
		Assert.Equal(Accessibility.Public, Assert.IsType<FunctionDeclaration>(result.Tree.Declarations[2]).Accessibility);
		Assert.Equal(Accessibility.Public, Assert.IsType<GlobalFieldDeclaration>(result.Tree.Declarations[3]).Accessibility);
	}

	[Fact]
	public void Uses_private_accessibility_for_type_members()
	{
		var result = Parser.Parse("type A { i32 x fun run -> i32 1 }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.Equal(Accessibility.Private, Assert.IsType<FieldMemberNode>(type.DeclaredMembers[0]).Accessibility);
		Assert.Equal(Accessibility.Private, Assert.IsType<FunctionDeclaration>(type.DeclaredMembers[1]).Accessibility);
	}

	[Fact]
	public void Captures_explicit_accessibility_on_top_level_and_members()
	{
		var result = Parser.Parse(
			"priv type A { pub i32 x prot fun p -> i32 1 int str label }\nint unit U\nprot fun helper { }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(result.Tree.Declarations[0]);
		Assert.Equal(Accessibility.Private, type.Accessibility);
		Assert.Equal(Accessibility.Public, Assert.IsType<FieldMemberNode>(type.DeclaredMembers[0]).Accessibility);
		Assert.Equal(Accessibility.Protected, Assert.IsType<FunctionDeclaration>(type.DeclaredMembers[1]).Accessibility);
		Assert.Equal(Accessibility.Internal, Assert.IsType<FieldMemberNode>(type.DeclaredMembers[2]).Accessibility);
		Assert.Equal(Accessibility.Internal, Assert.IsType<UnitDeclaration>(result.Tree.Declarations[1]).Accessibility);
		Assert.Equal(Accessibility.Protected, Assert.IsType<FunctionDeclaration>(result.Tree.Declarations[2]).Accessibility);
	}

	[Fact]
	public void Captures_override_flag_with_flexible_modifier_order()
	{
		var result = Parser.Parse(
			"type A { ~fun one -> i32 1 pub ~fun two -> i32 2 ~ pub fun three -> i32 3 }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var one = Assert.IsType<FunctionDeclaration>(type.DeclaredMembers[0]);
		Assert.True(one.IsOverride);
		Assert.Equal(Accessibility.Private, one.Accessibility);

		var two = Assert.IsType<FunctionDeclaration>(type.DeclaredMembers[1]);
		Assert.True(two.IsOverride);
		Assert.Equal(Accessibility.Public, two.Accessibility);

		var three = Assert.IsType<FunctionDeclaration>(type.DeclaredMembers[2]);
		Assert.True(three.IsOverride);
		Assert.Equal(Accessibility.Public, three.Accessibility);
	}

	[Fact]
	public void Captures_abstract_and_static_members()
	{
		var result = Parser.Parse(
			"pub @type Shape { pub @fun area() -> f64 #type Cache { } #fun twice(i32 n) -> i32 n * 2 #i32 count = 0 }");

		Assert.Empty(result.Diagnostics);
		var shape = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		Assert.True(shape.IsAbstract);
		Assert.Equal(Accessibility.Public, shape.Accessibility);

		var area = Assert.IsType<FunctionDeclaration>(shape.DeclaredMembers[0]);
		Assert.True(area.IsAbstract);
		Assert.Equal(Accessibility.Public, area.Accessibility);

		var cache = Assert.IsType<TypeDeclaration>(shape.DeclaredMembers[1]);
		Assert.True(cache.IsStatic);
		Assert.Equal(Accessibility.Private, cache.Accessibility);

		var twice = Assert.IsType<FunctionDeclaration>(shape.DeclaredMembers[2]);
		Assert.True(twice.IsStatic);

		var count = Assert.IsType<FieldMemberNode>(shape.DeclaredMembers[3]);
		Assert.True(count.IsStatic);
		Assert.Equal(Accessibility.Private, count.Accessibility);
	}

	[Fact]
	public void Captures_accessibility_on_static_nested_type_in_any_order()
	{
		var result = Parser.Parse("type A { #pub type Cache { } }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var cache = Assert.IsType<TypeDeclaration>(Assert.Single(type.DeclaredMembers));
		Assert.True(cache.IsStatic);
		Assert.Equal(Accessibility.Public, cache.Accessibility);
	}

	[Fact]
	public void Parses_auto_property_with_initializer()
	{
		var result = Parser.Parse("type Counter { i32 total { get; set } = 5 }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var property = Assert.IsType<PropertyMemberNode>(Assert.Single(type.DeclaredMembers));
		Assert.True(property.IsAuto);
		Assert.Equal([AccessorKind.Get, AccessorKind.Set], property.Accessors.Select(accessor => accessor.Kind));
		Assert.All(property.Accessors, accessor => Assert.Null(accessor.Body));
		Assert.NotNull(property.Initializer);
		Assert.Null(property.ExpressionBody);
	}

	[Fact]
	public void Parses_single_accessor_forms_without_body()
	{
		var result = Parser.Parse("type Sample { i32 a { get; } i32 b { set; } i32 c { init; } }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var properties = type.DeclaredMembers.Cast<PropertyMemberNode>().ToArray();
		Assert.Equal(3, properties.Length);
		Assert.True(properties[0].IsAuto);
		Assert.True(properties[1].IsAuto);
		Assert.True(properties[2].IsAuto);
		Assert.Equal(AccessorKind.Init, Assert.Single(properties[2].Accessors).Kind);
	}

	[Fact]
	public void Parses_accessor_expression_and_block_bodies()
	{
		var result = Parser.Parse(
			"type Sample { i32 a { get => 1 } i32 b { get { ret 2 } } i32 c { get => 1 set(value) { } } }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var array = Assert.IsType<PropertyMemberNode>(type.DeclaredMembers[0]);
		Assert.False(array.IsAuto);
		Assert.IsType<IntegerLiteralExpression>(Assert.Single(array.Accessors).Body);

		var block = Assert.IsType<PropertyMemberNode>(type.DeclaredMembers[1]);
		Assert.IsType<BlockStatement>(Assert.Single(block.Accessors).Body);

		var pair = Assert.IsType<PropertyMemberNode>(type.DeclaredMembers[2]);
		Assert.False(pair.IsAuto);
		Assert.Equal([AccessorKind.Get, AccessorKind.Set], pair.Accessors.Select(accessor => accessor.Kind));
		Assert.Single(pair.Accessors[1].Parameters);
	}

	[Fact]
	public void Parses_accessor_bodies_from_examples()
	{
		var result = Parser.Parse(
			"type Counter { i32 _val i32 val { get _val set(value) _val = value * 2 } }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var property = Assert.IsType<PropertyMemberNode>(type.DeclaredMembers[1]);
		Assert.False(property.IsAuto);

		var get = property.Accessors[0];
		Assert.Equal(AccessorKind.Get, get.Kind);
		Assert.Equal("_val", Assert.IsType<IdentifierExpression>(get.Body).Name);

		var set = property.Accessors[1];
		Assert.Equal(AccessorKind.Set, set.Kind);
		Assert.Equal("value", Assert.Single(set.Parameters).Name);
		var assignment = Assert.IsType<AssignmentStatement>(set.Body);
		Assert.Equal("_val", Assert.IsType<IdentifierExpression>(assignment.Target).Name);
		Assert.IsType<BinaryExpression>(assignment.Value);
	}

	[Fact]
	public void Keeps_expression_property()
	{
		var result = Parser.Parse("type Sample { i32 _val i32 doubled => _val * 2 }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var property = Assert.IsType<PropertyMemberNode>(type.DeclaredMembers[1]);
		Assert.Empty(property.Accessors);
		Assert.NotNull(property.ExpressionBody);
		Assert.False(property.IsAuto);
	}

	[Fact]
	public void Parses_constructor_chain()
	{
		var result = Parser.Parse("type Range(i32 lo, i32 hi) { ctor(i32 hi) : $(0, hi) }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var ctor = Assert.IsType<ConstructorMemberNode>(Assert.Single(type.DeclaredMembers));
		Assert.NotNull(ctor.Chain);
		Assert.Equal(2, ctor.Chain.Arguments.Count);
		Assert.IsType<IntegerLiteralExpression>(ctor.Chain.Arguments[0]);
		Assert.Equal("hi", Assert.IsType<IdentifierExpression>(ctor.Chain.Arguments[1]).Name);
	}

	[Fact]
	public void Rejects_non_dollar_constructor_chain()
	{
		var result = Parser.Parse("type Range(i32 lo) { ctor(i32 hi) : base(hi) { } }");

		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PAR018");
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var ctor = Assert.IsType<ConstructorMemberNode>(Assert.Single(type.DeclaredMembers));
		Assert.Null(ctor.Chain);
		Assert.NotNull(ctor.Body);
	}

	[Fact]
	public void Rejects_nonsense_modifiers()
	{
		var overrideType = Parser.Parse("~type A { }");
		var staticTopLevelFunction = Parser.Parse("#fun run { }");
		var staticField = Parser.Parse("type A { @i32 x }");
		var duplicate = Parser.Parse("pub pub fun run { }");
		var incompatible = Parser.Parse("type A { #~fun run -> i32 1 }");
		var overrideUnit = Parser.Parse("~unit U");

		Assert.Contains(overrideType.Diagnostics, diagnostic => diagnostic.Code == "PAR017");
		Assert.Contains(staticTopLevelFunction.Diagnostics, diagnostic => diagnostic.Code == "PAR017");
		Assert.Contains(staticField.Diagnostics, diagnostic => diagnostic.Code == "PAR017");
		Assert.Contains(duplicate.Diagnostics, diagnostic => diagnostic.Code == "PAR017");
		Assert.Contains(incompatible.Diagnostics, diagnostic => diagnostic.Code == "PAR017");
		Assert.Contains(overrideUnit.Diagnostics, diagnostic => diagnostic.Code == "PAR017");
	}

	[Fact]
	public void Rejects_unsupported_accessor_forms()
	{
		var unknown = Parser.Parse("type A { i32 x { nope; } }");
		var parameter = Parser.Parse("type A { i32 x { get(value) value } }");

		Assert.Contains(unknown.Diagnostics, diagnostic => diagnostic.Code == "PAR008");
		Assert.Contains(parameter.Diagnostics, diagnostic => diagnostic.Code == "PAR019");
	}

	[Fact]
	public void Parent_selections_keep_working_with_modifiers()
	{
		var result = Parser.Parse("type C()[A, B] { pub fun $[A].hello(i32 x) -> str priv str $[A].name }");

		Assert.Empty(result.Diagnostics);
		var type = Assert.IsType<TypeDeclaration>(Assert.Single(result.Tree.Declarations));
		var method = Assert.IsType<ParentMethodSelectionNode>(type.DeclaredMembers[0]);
		Assert.Equal("A", method.ParentType);
		Assert.Equal(Accessibility.Public, method.Accessibility);
		Assert.False(method.IsOverride);

		var field = Assert.IsType<ParentFieldSelectionNode>(type.DeclaredMembers[1]);
		Assert.Equal("name", field.Name);
		Assert.Equal(Accessibility.Private, field.Accessibility);
		Assert.False(field.IsOverride);
	}
}
