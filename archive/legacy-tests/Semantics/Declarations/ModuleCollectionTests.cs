using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Xunit;

namespace Kern.Tests.Semantics.Declarations;

public sealed class ModuleCollectionTests
{
	[Fact]
	public void Collects_top_level_types_units_functions_and_globals()
	{
		var module = DeclarationTestSupport.Module(
			"type Person {} unit Meter {} unit! Second {} fun calculate(i32 x) -> i32 { x } fun calculate(str x) {} i32! limit = 5");

		Assert.Equal("test", module.Name);
		Assert.Equal(["Meter", "Person", "Second"], module.Types.Keys.OrderBy(name => name).ToArray());
		Assert.Equal(2, module.GetFunctions("calculate").Count);
		Assert.True(module.FindGlobal("limit")!.IsConst);
		Assert.Equal(module.Types["Person"], module.FindType("Person"));
	}

	[Theory]
	[InlineData("type Person { i32 age }", Accessibility.Public, Accessibility.Private)]
	[InlineData("priv type Person { pub i32 age }", Accessibility.Private, Accessibility.Public)]
	[InlineData("int type Person { prot i32 age }", Accessibility.Internal, Accessibility.Protected)]
	public void Captures_accessibility_defaults_and_modifiers(
		string source,
		Accessibility typeAccessibility,
		Accessibility fieldAccessibility)
	{
		var type = DeclarationTestSupport.Type(source, "Person");
		var field = Assert.IsType<FieldSymbol>(type.Members.FindField("age"));

		Assert.Equal(typeAccessibility, type.Accessibility);
		Assert.Equal(fieldAccessibility, field.Accessibility);
	}

	[Fact]
	public void Top_level_functions_and_globals_are_public_by_default()
	{
		var module = DeclarationTestSupport.Module("fun hello() {} str greeting = \"hi\"");

		Assert.Equal(Accessibility.Public, Assert.Single(module.GetFunctions("hello")).Accessibility);
		Assert.Equal(Accessibility.Public, module.FindGlobal("greeting")!.Accessibility);
		Assert.True(module.FindGlobal("greeting")!.IsStatic);
	}

	[Fact]
	public void Keeps_required_global_as_const_and_ordinary_global_as_mutable()
	{
		var module = DeclarationTestSupport.Module("i32! limit = 5 i32 counter = 0");

		Assert.True(module.FindGlobal("limit")!.IsConst);
		Assert.True(module.FindGlobal("limit")!.IsRequired);
		Assert.False(module.FindGlobal("counter")!.IsConst);
		Assert.False(module.FindGlobal("counter")!.IsRequired);
	}

	[Fact]
	public void Captures_type_metadata_and_unit_value_semantics()
	{
		var module = DeclarationTestSupport.Module(
			"@type Base { } open type Open { } unit Meter { } unit! Frozen { } type Point { } type Inh[Point] { }");

		var baseType = module.FindType("Base")!;
		Assert.True(baseType.IsAbstract);
		Assert.Equal(SymbolKind.AbstractClass, baseType.Kind);

		Assert.True(module.FindType("Open")!.IsOpen);
		Assert.False(module.FindType("Open")!.IsAbstract);

		var meter = module.FindType("Meter")!;
		Assert.True(meter.IsValueType);
		Assert.False(meter.IsImmutable);
		Assert.Empty(meter.Bases);

		var frozen = module.FindType("Frozen")!;
		Assert.True(frozen.IsValueType);
		Assert.True(frozen.IsImmutable);
		Assert.Equal(SymbolKind.ImmutableUnit, frozen.Kind);

		Assert.False(module.FindType("Point")!.IsValueType);
		Assert.Equal(["Point"], module.FindType("Inh")!.Bases.Select(item => item.ToString()));
	}

	[Fact]
	public void Resolves_union_alias_alternatives_and_keeps_them_out_of_members()
	{
		var module = DeclarationTestSupport.Module(
			"type Circle(f64 radius) { } type Rect(f64 width, f64 height) { } type Shape = [Circle, Rect]");

		var shape = module.FindType("Shape")!;

		Assert.Equal(SymbolKind.UnionAlias, shape.Kind);
		Assert.Equal(["Circle", "Rect"], shape.UnionAlternatives.Select(item => item.ToString()));
		Assert.Empty(shape.Members.Functions);
		Assert.Empty(shape.Members.Fields);
		Assert.Empty(shape.Bases);
	}

	[Fact]
	public void Uses_the_supplied_module_name_for_types()
	{
		var module = DeclarationTestSupport.Module("type Person {}", "app.people");

		Assert.Equal("app.people", module.Name);
		Assert.Equal("app.people", module.FindType("Person")!.ModuleName);
	}

	[Fact]
	public void Groups_top_level_function_overloads_by_name()
	{
		var module = DeclarationTestSupport.Module("fun pick(i32 x) {} fun pick(str x) {} fun pick(i32 x, i32 y) {}");

		Assert.Equal(
			["pick(i32)", "pick(str)", "pick(i32,i32)"],
			module.GetFunctions("pick").Select(function => function.Signature));
		Assert.All(module.GetFunctions("pick"), function => Assert.Equal(FunctionKind.Function, function.Kind));
	}
}
