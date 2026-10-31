using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Xunit;
using TypeConversions = Kern.Compiler.Semantics.Types.Conversions.Conversions;

namespace Kern.Tests.Semantics.Types.Conversions;

public sealed class ReferenceConversionTests
{
	[Fact]
	public void Nullable_wraps_the_same_type_only()
	{
		var nullable = NullableType.Create(PrimitiveType.I32);

		var conversion = ConversionTestSupport.Classify(PrimitiveType.I32, nullable);
		Assert.Equal(ConversionKind.NullableWrap, conversion.Kind);
		Assert.Equal(3, conversion.Rank);

		Assert.Equal(ConversionKind.Identity, ConversionTestSupport.Classify(nullable, nullable).Kind);
		ConversionTestSupport.Rejects(nullable, PrimitiveType.I32);
	}

	[Fact]
	public void Lifted_conversion_composes_with_widening()
	{
		Assert.Equal(
			ConversionKind.Lifted,
			ConversionTestSupport.Classify(PrimitiveType.I32, NullableType.Create(PrimitiveType.I64)).Kind);
		Assert.Equal(
			ConversionKind.Lifted,
			ConversionTestSupport.Classify(PrimitiveType.I32, NullableType.Create(PrimitiveType.F64)).Kind);

		ConversionTestSupport.Rejects(PrimitiveType.I64, NullableType.Create(PrimitiveType.I32));
		ConversionTestSupport.Rejects(PrimitiveType.Str, NullableType.Create(PrimitiveType.I32));
	}

	[Fact]
	public void Any_value_boxes_to_object()
	{
		Assert.Equal(ConversionKind.Boxing, ConversionTestSupport.Classify(PrimitiveType.I32, PrimitiveType.Object).Kind);
		Assert.Equal(ConversionKind.Boxing, ConversionTestSupport.Classify(NullableType.Create(PrimitiveType.I32), PrimitiveType.Object).Kind);
		Assert.Equal(ConversionKind.Boxing, ConversionTestSupport.Classify(ConversionTestSupport.List(PrimitiveType.I32), PrimitiveType.Object).Kind);
		Assert.Equal(ConversionKind.Boxing, ConversionTestSupport.Classify(PrimitiveType.Str, PrimitiveType.Object).Kind);

		ConversionTestSupport.Rejects(PrimitiveType.Object, PrimitiveType.I32);
	}

	[Fact]
	public void Nil_is_assignable_only_to_nullable()
	{
		Assert.True(TypeConversions.IsNilAssignable(NullableType.Create(PrimitiveType.I32)));
		Assert.False(TypeConversions.IsNilAssignable(PrimitiveType.Object));

		Assert.False(TypeConversions.IsNilAssignable(PrimitiveType.I32));
		Assert.False(TypeConversions.IsNilAssignable(PrimitiveType.Str));
		Assert.False(TypeConversions.IsNilAssignable(ConversionTestSupport.List(PrimitiveType.I32)));
		Assert.False(TypeConversions.IsNilAssignable(
			UnionType.Create([NullableType.Create(PrimitiveType.I32), PrimitiveType.Str])));
	}

	[Fact]
	public void None_does_not_convert_to_any_other_type()
	{
		Assert.Equal(ConversionKind.Identity, ConversionTestSupport.Classify(PrimitiveType.None, PrimitiveType.None).Kind);

		ConversionTestSupport.Rejects(PrimitiveType.None, PrimitiveType.I32);
		ConversionTestSupport.Rejects(PrimitiveType.None, PrimitiveType.Str);
		ConversionTestSupport.Rejects(PrimitiveType.None, PrimitiveType.Object);
		ConversionTestSupport.Rejects(PrimitiveType.None, NullableType.Create(PrimitiveType.I32));
	}

	[Fact]
	public void Union_target_and_union_upcast()
	{
		var union = UnionType.Create([PrimitiveType.I32, PrimitiveType.Str]);

		Assert.Equal(ConversionKind.UnionTarget, ConversionTestSupport.Classify(PrimitiveType.I32, union).Kind);
		Assert.Equal(ConversionKind.UnionTarget, ConversionTestSupport.Classify(PrimitiveType.Str, union).Kind);
		ConversionTestSupport.Rejects(PrimitiveType.Bool, union);

		Assert.Equal(ConversionKind.Boxing, ConversionTestSupport.Classify(union, PrimitiveType.Object).Kind);

		var numeric = UnionType.Create([PrimitiveType.I32, PrimitiveType.I64]);
		Assert.Equal(ConversionKind.UnionUpcast, ConversionTestSupport.Classify(numeric, PrimitiveType.I64).Kind);
		ConversionTestSupport.Rejects(numeric, PrimitiveType.I32);
		ConversionTestSupport.Rejects(union, PrimitiveType.I64);

		var wider = UnionType.Create([PrimitiveType.I32, PrimitiveType.Str, PrimitiveType.Bool]);
		Assert.Equal(ConversionKind.UnionUpcast, ConversionTestSupport.Classify(union, wider).Kind);
		ConversionTestSupport.Rejects(wider, union);
		Assert.Equal(ConversionKind.Identity, ConversionTestSupport.Classify(union, union).Kind);
	}

	[Fact]
	public void Tuples_convert_element_wise_and_ignore_names()
	{
		var source = new TupleType(
			[new TupleElement(PrimitiveType.I32, "x"), new TupleElement(PrimitiveType.Str, "y")]);
		var target = new TupleType(
			[new TupleElement(PrimitiveType.I64, "a"), new TupleElement(PrimitiveType.Str, "b")]);
		var unnamed = new TupleType(
			[new TupleElement(PrimitiveType.I32, null), new TupleElement(PrimitiveType.Str, null)]);

		Assert.Equal(ConversionKind.TupleUpcast, ConversionTestSupport.Classify(source, target).Kind);
		Assert.Equal(ConversionKind.TupleUpcast, ConversionTestSupport.Classify(source, unnamed).Kind);

		var differentArity = new TupleType(
			[new TupleElement(PrimitiveType.I32, "x"), new TupleElement(PrimitiveType.Str, "y"), new TupleElement(PrimitiveType.Bool, "z")]);
		ConversionTestSupport.Rejects(source, differentArity);

		var incompatible = new TupleType(
			[new TupleElement(PrimitiveType.I32, "x"), new TupleElement(PrimitiveType.I32, "y")]);
		ConversionTestSupport.Rejects(source, incompatible);
	}

	[Fact]
	public void Function_types_are_identity_only()
	{
		var function = new FunctionType([PrimitiveType.I32], PrimitiveType.Str);

		Assert.Equal(ConversionKind.Identity, ConversionTestSupport.Classify(function, function).Kind);

		ConversionTestSupport.Rejects(function, new FunctionType([PrimitiveType.I64], PrimitiveType.Str));
		ConversionTestSupport.Rejects(function, new FunctionType([PrimitiveType.I32], PrimitiveType.Object));
		ConversionTestSupport.Rejects(function, new FunctionType([PrimitiveType.I32, PrimitiveType.I32], PrimitiveType.Str));
	}

	[Fact]
	public void Function_types_do_not_vary_in_parameter_or_result_types()
	{
		// Q3: вариантности нет — ни контравариантности параметров, ни ковариантности результата.
		var resolver = TypeTestSupport.ResolverFor("type Base { } type Derived[Base] { }");
		var baseType = Class(resolver, "Base");
		var derived = Class(resolver, "Derived");
		Assert.Empty(resolver.Diagnostics);

		// Результат: fun() -> Derived не подходит под fun() -> Base.
		ConversionTestSupport.Rejects(
			new FunctionType([], derived),
			new FunctionType([], baseType));

		// Параметры: ни fun(Base) -> void под fun(Derived) -> void, ни наоборот.
		ConversionTestSupport.Rejects(
			new FunctionType([baseType], PrimitiveType.Void),
			new FunctionType([derived], PrimitiveType.Void));
		ConversionTestSupport.Rejects(
			new FunctionType([derived], PrimitiveType.Void),
			new FunctionType([baseType], PrimitiveType.Void));

		// Тождественная подпись принимается, даже будучи другим экземпляром типа.
		Assert.Equal(
			ConversionKind.Identity,
			ConversionTestSupport.Classify(
				new FunctionType([derived], baseType),
				new FunctionType([derived], baseType)).Kind);
	}

	[Fact]
	public void Lifted_conversion_accepts_a_class_upcast()
	{
		// Q8: подъём S -> T? работает при любом неявном S -> T, включая Child -> Base.
		var resolver = TypeTestSupport.ResolverFor("type Base { } type Child[Base] { }");
		var baseType = Class(resolver, "Base");
		var child = Class(resolver, "Child");
		Assert.Empty(resolver.Diagnostics);

		var lifted = ConversionTestSupport.Classify(child, NullableType.Create(baseType));
		Assert.Equal(ConversionKind.Lifted, lifted.Kind);
		Assert.Equal(4, lifted.Rank);

		Assert.Equal(
			ConversionKind.NullableWrap,
			ConversionTestSupport.Classify(baseType, NullableType.Create(baseType)).Kind);

		// Обратное направление не становится неявным: T? -> T запрещено.
		ConversionTestSupport.Rejects(NullableType.Create(baseType), child);
	}

	[Fact]
	public void Error_and_never_do_not_cascade()
	{
		var errorToValue = ConversionTestSupport.Classify(ErrorType.Instance, PrimitiveType.I32);
		Assert.Equal(ConversionKind.ErrorSource, errorToValue.Kind);
		Assert.Equal(99, errorToValue.Rank);

		Assert.Equal(ConversionKind.ErrorSource, ConversionTestSupport.Classify(PrimitiveType.I32, ErrorType.Instance).Kind);
		Assert.Equal(ConversionKind.ErrorSource, ConversionTestSupport.Classify(ErrorType.Instance, ErrorType.Instance).Kind);

		Assert.Equal(ConversionKind.NeverSource, ConversionTestSupport.Classify(PrimitiveType.Never, PrimitiveType.I32).Kind);
		Assert.Equal(ConversionKind.NeverSource, ConversionTestSupport.Classify(PrimitiveType.Never, PrimitiveType.Object).Kind);
		Assert.Equal(ConversionKind.NeverSource,
			ConversionTestSupport.Classify(PrimitiveType.Never, NullableType.Create(PrimitiveType.Str)).Kind);
	}

	[Fact]
	public void Conversion_ranks_are_stable_for_overload_resolution()
	{
		Assert.Equal(0, Conversion.Implicit(ConversionKind.Identity).Rank);
		Assert.Equal(1, Conversion.Implicit(ConversionKind.NumericConstant).Rank);
		Assert.Equal(2, Conversion.Implicit(ConversionKind.NumericWidening).Rank);
		Assert.Equal(3, Conversion.Implicit(ConversionKind.NullableWrap).Rank);
		Assert.Equal(4, Conversion.Implicit(ConversionKind.BaseUpcast).Rank);
		Assert.Equal(4, Conversion.Implicit(ConversionKind.CollectionUpcast).Rank);
		Assert.Equal(4, Conversion.Implicit(ConversionKind.Lifted).Rank);
		Assert.Equal(5, Conversion.Implicit(ConversionKind.TupleUpcast).Rank);
		Assert.Equal(5, Conversion.Implicit(ConversionKind.FunctionUpcast).Rank);
		Assert.Equal(6, Conversion.Implicit(ConversionKind.UnionTarget).Rank);
		Assert.Equal(6, Conversion.Implicit(ConversionKind.UnionUpcast).Rank);
		Assert.Equal(7, Conversion.Implicit(ConversionKind.Boxing).Rank);
		Assert.Equal(8, Conversion.Implicit(ConversionKind.StrToListChar).Rank);
		Assert.Equal(8, Conversion.Implicit(ConversionKind.ListCharToStr).Rank);
		Assert.Equal(99, Conversion.Implicit(ConversionKind.ErrorSource).Rank);
	}

	private static ClassType Class(TypeResolver resolver, string text) =>
		Assert.IsType<ClassType>(resolver.Resolve(TypeTestSupport.ParseType(text)));
}
