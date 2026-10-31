using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Xunit;

namespace Kern.Tests.Semantics.Types.Conversions;

public sealed class InheritanceConversionTests
{
	private const string Source =
		"type Root { } type Mid[Root] { } type Leaf[Mid] { } " +
		"type Box<T> { } type Parent<T> { } type Child<T>[Parent<T>] { } " +
		"type Grand<T> { } type Mid2<T>[Grand<T>] { } type Leaf2<T>[Mid2<T>] { } " +
		"unit Meter { }";

	[Fact]
	public void Classes_upcast_through_several_base_levels()
	{
		var resolver = TypeTestSupport.ResolverFor(Source);
		var leaf = Class(resolver, "Leaf");
		var mid = Class(resolver, "Mid");
		var root = Class(resolver, "Root");
		Assert.Empty(resolver.Diagnostics);

		Assert.Equal(ConversionKind.BaseUpcast, ConversionTestSupport.Classify(leaf, mid).Kind);
		Assert.Equal(ConversionKind.BaseUpcast, ConversionTestSupport.Classify(leaf, root).Kind);
		Assert.Equal(ConversionKind.BaseUpcast, ConversionTestSupport.Classify(mid, root).Kind);
		Assert.Equal(ConversionKind.Identity, ConversionTestSupport.Classify(root, root).Kind);

		ConversionTestSupport.Rejects(root, leaf);
		ConversionTestSupport.Rejects(mid, leaf);
	}

	[Fact]
	public void Generic_bases_are_substituted_with_the_source_arguments()
	{
		var resolver = TypeTestSupport.ResolverFor(Source);
		var child = Class(resolver, "Child<i32>");
		var parentI32 = Class(resolver, "Parent<i32>");
		var parentI64 = Class(resolver, "Parent<i64>");
		Assert.Empty(resolver.Diagnostics);

		Assert.Equal(ConversionKind.BaseUpcast, ConversionTestSupport.Classify(child, parentI32).Kind);
		ConversionTestSupport.Rejects(child, parentI64);
		ConversionTestSupport.Rejects(child, Class(resolver, "Parent<str>"));
	}

	[Fact]
	public void Multi_level_generic_bases_are_substituted()
	{
		var resolver = TypeTestSupport.ResolverFor(Source);
		var leaf = Class(resolver, "Leaf2<i32>");
		var grand = Class(resolver, "Grand<i32>");
		Assert.Empty(resolver.Diagnostics);

		Assert.Equal(ConversionKind.BaseUpcast, ConversionTestSupport.Classify(leaf, grand).Kind);
		ConversionTestSupport.Rejects(leaf, Class(resolver, "Grand<str>"));
	}

	[Fact]
	public void Diamond_with_the_same_generic_parent_at_two_instantiations_is_walked_per_arguments()
	{
		var resolver = TypeTestSupport.ResolverFor(
			"type Base<T> { } type Mid<T>[Base<T>] { } type Two[Mid<str>, Mid<i32>] { }");
		var two = Class(resolver, "Two");
		Assert.Empty(resolver.Diagnostics);

		Assert.Equal(ConversionKind.BaseUpcast, ConversionTestSupport.Classify(two, Class(resolver, "Base<str>")).Kind);
		Assert.Equal(ConversionKind.BaseUpcast, ConversionTestSupport.Classify(two, Class(resolver, "Base<i32>")).Kind);
		ConversionTestSupport.Rejects(two, Class(resolver, "Base<bool>"));
	}

	[Fact]
	public void Class_generic_arguments_are_invariant()
	{
		var resolver = TypeTestSupport.ResolverFor(Source);
		var boxI32 = Class(resolver, "Box<i32>");
		var boxStr = Class(resolver, "Box<str>");

		ConversionTestSupport.Rejects(boxI32, boxStr);
		Assert.Equal(ConversionKind.Identity, ConversionTestSupport.Classify(boxI32, Class(resolver, "Box<i32>")).Kind);
	}

	[Fact]
	public void Units_have_no_bases_and_immutable_unit_erases_to_unit()
	{
		var resolver = TypeTestSupport.ResolverFor(Source);
		var meter = Unit(resolver, "Meter");

		Assert.Empty(meter.Symbol.Bases);
		Assert.Equal(ConversionKind.Identity, ConversionTestSupport.Classify(meter, meter).Kind);
		Assert.Equal(ConversionKind.Boxing, ConversionTestSupport.Classify(meter, PrimitiveType.Object).Kind);

		var immutable = new UnitType(meter.Symbol, meter.Arguments, isImmutable: true);
		var erasure = ConversionTestSupport.Classify(immutable, meter);
		Assert.Equal(ConversionKind.UnitImmutableErase, erasure.Kind);
		Assert.Equal(4, erasure.Rank);
		ConversionTestSupport.Rejects(meter, immutable);
	}

	private static ClassType Class(TypeResolver resolver, string text) =>
		Assert.IsType<ClassType>(resolver.Resolve(TypeTestSupport.ParseType(text)));

	private static UnitType Unit(TypeResolver resolver, string text) =>
		Assert.IsType<UnitType>(resolver.Resolve(TypeTestSupport.ParseType(text)));
}
