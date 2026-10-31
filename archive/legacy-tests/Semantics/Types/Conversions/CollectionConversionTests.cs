using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Xunit;

namespace Kern.Tests.Semantics.Types.Conversions;

public sealed class CollectionConversionTests
{
	[Fact]
	public void List_array_and_set_upcast_to_collection()
	{
		var collection = ConversionTestSupport.Collection(PrimitiveType.I32);

		Assert.Equal(ConversionKind.CollectionUpcast, ConversionTestSupport.Classify(ConversionTestSupport.List(PrimitiveType.I32), collection).Kind);
		Assert.Equal(ConversionKind.CollectionUpcast, ConversionTestSupport.Classify(ConversionTestSupport.Array(PrimitiveType.I32), collection).Kind);
		Assert.Equal(ConversionKind.CollectionUpcast, ConversionTestSupport.Classify(ConversionTestSupport.Set(PrimitiveType.I32), collection).Kind);
		Assert.Equal(ConversionKind.Identity, ConversionTestSupport.Classify(collection, collection).Kind);
	}

	[Fact]
	public void Collection_upcast_converts_the_element_type()
	{
		var collection = ConversionTestSupport.Collection(PrimitiveType.I64);

		Assert.Equal(ConversionKind.CollectionUpcast, ConversionTestSupport.Classify(ConversionTestSupport.List(PrimitiveType.I32), collection).Kind);
		ConversionTestSupport.Rejects(ConversionTestSupport.List(PrimitiveType.I32), ConversionTestSupport.Collection(PrimitiveType.Str));
	}

	[Fact]
	public void Strings_upcast_to_collections_of_char()
	{
		var conversion = ConversionTestSupport.Classify(PrimitiveType.Str, ConversionTestSupport.Collection(PrimitiveType.Char));

		Assert.Equal(ConversionKind.CollectionUpcast, conversion.Kind);
		ConversionTestSupport.Rejects(PrimitiveType.Str, ConversionTestSupport.Collection(PrimitiveType.U8));
	}

	[Fact]
	public void Dict_has_no_invented_collection_parent()
	{
		var dict = ConversionTestSupport.Dict(PrimitiveType.Str, PrimitiveType.I32);

		ConversionTestSupport.Rejects(dict, ConversionTestSupport.Collection(PrimitiveType.I32));
		ConversionTestSupport.Rejects(dict, ConversionTestSupport.Collection(PrimitiveType.Str));
	}

	[Fact]
	public void Other_collection_conversions_are_not_implicit()
	{
		ConversionTestSupport.Rejects(
			ConversionTestSupport.List(PrimitiveType.I32),
			ConversionTestSupport.Set(PrimitiveType.I32));
		ConversionTestSupport.Rejects(
			ConversionTestSupport.Collection(PrimitiveType.I32),
			ConversionTestSupport.List(PrimitiveType.I32));
		ConversionTestSupport.Rejects(
			ConversionTestSupport.Array(PrimitiveType.I32),
			ConversionTestSupport.List(PrimitiveType.I32));
	}

	[Fact]
	public void Str_and_list_char_are_contextual_only()
	{
		var listOfChar = ConversionTestSupport.List(PrimitiveType.Char);

		ConversionTestSupport.Rejects(PrimitiveType.Str, listOfChar);
		Assert.Equal(ConversionKind.StrToListChar, ConversionTestSupport.ClassifyContextual(PrimitiveType.Str, listOfChar).Kind);

		ConversionTestSupport.Rejects(listOfChar, PrimitiveType.Str);
		Assert.Equal(ConversionKind.ListCharToStr, ConversionTestSupport.ClassifyContextual(listOfChar, PrimitiveType.Str).Kind);

		ConversionTestSupport.RejectsContextual(ConversionTestSupport.List(PrimitiveType.I32), PrimitiveType.Str);
	}

	[Fact]
	public void A_typed_char_list_is_the_same_type_the_resolver_produces()
	{
		var resolved = TypeTestSupport.Resolve("", "list<char>");

		Assert.Equal(ConversionTestSupport.List(PrimitiveType.Char), resolved);
		Assert.Equal(ConversionKind.ListCharToStr, ConversionTestSupport.ClassifyContextual(resolved, PrimitiveType.Str).Kind);
	}

	[Fact]
	public void Contextual_classification_keeps_ordinary_implicit_conversions()
	{
		var conversion = ConversionTestSupport.ClassifyContextual(PrimitiveType.I32, PrimitiveType.I64);

		Assert.Equal(ConversionKind.NumericWidening, conversion.Kind);
	}
}
