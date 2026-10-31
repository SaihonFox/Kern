using Kern.Compiler.Semantics.Types;
using Xunit;

namespace Kern.Tests.Semantics.Types;

public sealed class TypeModelTests
{
	[Fact]
	public void Types_with_the_same_shape_are_equal_but_arguments_matter()
	{
		var first = new BuiltinGenericType(BuiltinGenericKind.List, [PrimitiveType.I32]);
		var second = new BuiltinGenericType(BuiltinGenericKind.List, [PrimitiveType.I32]);
		var other = new BuiltinGenericType(BuiltinGenericKind.List, [PrimitiveType.Str]);

		Assert.Equal(first, second);
		Assert.Equal(first.GetHashCode(), second.GetHashCode());
		Assert.NotEqual(first, other);
		Assert.Equal("list<i32>", first.ToString());
		Assert.Equal("list<str>", other.ToString());
	}

	[Fact]
	public void Class_types_compare_their_symbol_and_arguments()
	{
		var symbol = new TypeSymbol { Name = "Box", Kind = SymbolKind.Class, GenericParameterNames = ["T"] };
		var first = new ClassType(symbol, [PrimitiveType.I32]);
		var second = new ClassType(symbol, [PrimitiveType.I32]);
		var other = new ClassType(symbol, [PrimitiveType.Str]);

		Assert.Equal(first, second);
		Assert.NotEqual(first, other);
		Assert.Equal("Box<i32>", first.ToString());
	}

	[Fact]
	public void Nullable_is_idempotent_and_absorbs_errors()
	{
		var once = NullableType.Create(PrimitiveType.I32);
		Assert.IsType<NullableType>(once);
		Assert.Equal("i32?", once.ToString());

		Assert.Same(once, NullableType.Create(once));
		Assert.Same(ErrorType.Instance, NullableType.Create(ErrorType.Instance));
		Assert.True(NullableType.Create(ErrorType.Instance) is ErrorType);

		var equivalent = NullableType.Create(PrimitiveType.I32);
		Assert.Equal(once, equivalent);
	}

	[Fact]
	public void Union_flattens_deduplicates_and_collapses()
	{
		var union = UnionType.Create([PrimitiveType.Str, PrimitiveType.I32, PrimitiveType.Str]);
		Assert.Equal("[i32,str]", union.ToString());
		Assert.Equal(2, Assert.IsType<UnionType>(union).Alternatives.Count);

		var nested = UnionType.Create([union, PrimitiveType.Bool]);
		Assert.Equal("[bool,i32,str]", nested.ToString());

		Assert.Same(PrimitiveType.I32, UnionType.Create([PrimitiveType.I32]));
		Assert.IsType<ErrorType>(UnionType.Create([]));
		Assert.IsType<ErrorType>(UnionType.Create([PrimitiveType.I32, ErrorType.Instance]));
	}

	[Fact]
	public void Parameter_types_compare_by_name_only()
	{
		var constrained = new TypeParameterType("T", [PrimitiveType.Num]);
		var plain = new TypeParameterType("T", []);
		var other = new TypeParameterType("U", []);

		Assert.Equal(constrained, plain);
		Assert.NotEqual(constrained, other);
		Assert.Equal("T", constrained.ToString());
		Assert.True(constrained.Satisfies(PrimitiveType.Num));
		Assert.True(constrained.Satisfies(PrimitiveType.Inum));
		Assert.False(constrained.Satisfies(PrimitiveType.I32));
	}

	[Fact]
	public void Parameter_scope_is_immutable_and_looks_up_by_name()
	{
		var empty = TypeParameterScope.Empty;
		var withT = empty.Add("T", [PrimitiveType.Num]);
		var withBoth = withT.Add("U");

		Assert.Equal(0, empty.Count);
		Assert.Equal(2, withBoth.Count);
		Assert.Null(empty.Lookup("T"));

		Assert.True(withT.TryLookup("T", out var parameter));
		Assert.Equal("T", parameter.ToString());
		Assert.Equal([PrimitiveType.Num], parameter.Constraints);

		Assert.True(withBoth.TryLookup("U", out var other));
		Assert.NotEqual(parameter, other);
		Assert.Equal(["T", "U"], withBoth.Names.Order());
	}

	[Fact]
	public void Type_symbol_carries_kind_and_generic_parameter_names()
	{
		var symbol = new TypeSymbol
		{
			Name = "Pair",
			Kind = SymbolKind.Class,
			GenericParameterNames = ["K", "V"]
		};

		Assert.Equal(2, symbol.GenericArity);
		Assert.Equal(["K", "V"], symbol.GenericParameterNames);
		Assert.Null(symbol.Declaration);
		Assert.Null(symbol.ModuleName);
	}
}
