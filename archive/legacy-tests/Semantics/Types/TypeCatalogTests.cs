using Kern.Compiler.Semantics.Types;
using Xunit;

namespace Kern.Tests.Semantics.Types;

public sealed class TypeCatalogTests
{
	private static readonly string[] NameablePrimitives =
	[
		"i8", "i16", "i32", "i64",
		"u8", "u16", "u32", "u64",
		"f16", "f32", "f64",
		"bool", "char", "str", "void", "object",
		"inum", "fnum", "num", "never"
	];

	[Fact]
	public void Catalog_exposes_every_nameable_primitive_with_canonical_text()
	{
		foreach (var name in NameablePrimitives)
		{
			Assert.True(TypeCatalog.TryGetPrimitive(name, out var primitive), name);
			Assert.Equal(name, primitive.ToString());
			Assert.Equal(TypeKind.Primitive, primitive.Kind);
		}

		Assert.Equal(NameablePrimitives.Length, TypeCatalog.PrimitiveNames.Count);
		Assert.Same(PrimitiveType.I32, TypeCatalog.GetPrimitive("i32"));
	}

	[Fact]
	public void None_and_literal_names_are_not_resolvable_types()
	{
		Assert.False(TypeCatalog.TryGetPrimitive("none", out _));
		Assert.False(TypeCatalog.TryGetPrimitive("nil", out _));
		Assert.False(TypeCatalog.TryGetPrimitiveKind("none", out _));
		Assert.Throws<ArgumentException>(() => TypeCatalog.GetPrimitive("none"));
	}

	[Fact]
	public void Primitive_singletons_cover_every_primitive_kind()
	{
		foreach (var kind in Enum.GetValues<PrimitiveKind>())
		{
			var primitive = PrimitiveType.For(kind);
			Assert.Equal(kind, primitive.Primitive);
			Assert.Equal(TypeKind.Primitive, primitive.Kind);
		}

		Assert.Equal("none", PrimitiveType.None.ToString());
	}

	[Fact]
	public void Catalog_maps_builtin_generics_names_arities_and_the_map_alias()
	{
		var expected = new Dictionary<BuiltinGenericKind, (string Name, int Arity)>
		{
			[BuiltinGenericKind.List] = ("list", 1),
			[BuiltinGenericKind.Array] = ("array", 1),
			[BuiltinGenericKind.Dict] = ("dict", 2),
			[BuiltinGenericKind.Set] = ("set", 1),
			[BuiltinGenericKind.Result] = ("result", 2),
			[BuiltinGenericKind.Task] = ("task", 1),
			[BuiltinGenericKind.Event] = ("event", 1),
			[BuiltinGenericKind.Observable] = ("observable", 1),
			[BuiltinGenericKind.Channel] = ("channel", 1),
			[BuiltinGenericKind.Pipe] = ("pipe", 1),
			[BuiltinGenericKind.Shared] = ("shared", 1),
			[BuiltinGenericKind.Iter] = ("iter", 1),
			[BuiltinGenericKind.AsyncIter] = ("async_iter", 1),
			[BuiltinGenericKind.Collection] = ("collection", 1)
		};

		foreach (var (kind, (name, arity)) in expected)
		{
			Assert.True(TypeCatalog.TryGetBuiltin(name, out var resolved), name);
			Assert.Equal(kind, resolved);
			Assert.Equal(arity, BuiltinGenericType.GetArity(kind));
			Assert.Equal(name, BuiltinGenericType.GetCanonicalName(kind));
		}

		Assert.True(TypeCatalog.TryGetBuiltin("map", out var map));
		Assert.Equal(BuiltinGenericKind.Dict, map);
		Assert.False(TypeCatalog.TryGetBuiltin("unknown", out _));
	}

	[Fact]
	public void Builtin_generic_types_render_canonical_text()
	{
		Assert.Equal("list<i32>", new BuiltinGenericType(BuiltinGenericKind.List, [PrimitiveType.I32]).ToString());
		Assert.Equal("array<str>", new BuiltinGenericType(BuiltinGenericKind.Array, [PrimitiveType.Str]).ToString());
		Assert.Equal("dict<str,i32>", new BuiltinGenericType(BuiltinGenericKind.Dict, [PrimitiveType.Str, PrimitiveType.I32]).ToString());
		Assert.Equal("set<i32>", new BuiltinGenericType(BuiltinGenericKind.Set, [PrimitiveType.I32]).ToString());
		Assert.Equal("result<i32,str>", new BuiltinGenericType(BuiltinGenericKind.Result, [PrimitiveType.I32, PrimitiveType.Str]).ToString());
		Assert.Equal("task<i32>", new BuiltinGenericType(BuiltinGenericKind.Task, [PrimitiveType.I32]).ToString());
		Assert.Equal("event<i32>", new BuiltinGenericType(BuiltinGenericKind.Event, [PrimitiveType.I32]).ToString());
		Assert.Equal("observable<i32>", new BuiltinGenericType(BuiltinGenericKind.Observable, [PrimitiveType.I32]).ToString());
		Assert.Equal("channel<i32>", new BuiltinGenericType(BuiltinGenericKind.Channel, [PrimitiveType.I32]).ToString());
		Assert.Equal("pipe<i32>", new BuiltinGenericType(BuiltinGenericKind.Pipe, [PrimitiveType.I32]).ToString());
		Assert.Equal("shared<i32>", new BuiltinGenericType(BuiltinGenericKind.Shared, [PrimitiveType.I32]).ToString());
		Assert.Equal("collection<char>", new BuiltinGenericType(BuiltinGenericKind.Collection, [PrimitiveType.Char]).ToString());
	}

	[Fact]
	public void Builtin_generic_kind_is_not_hidden_by_a_shadowing_property()
	{
		var list = new BuiltinGenericType(BuiltinGenericKind.List, [PrimitiveType.I32]);

		Assert.Equal(TypeKind.BuiltinGeneric, list.Kind);
		Assert.Equal(BuiltinGenericKind.List, list.BuiltinKind);
		Assert.Equal(typeof(BuiltinGenericKind), typeof(BuiltinGenericType).GetProperty("BuiltinKind")!.PropertyType);
		Assert.Null(typeof(BuiltinGenericType).GetProperty("Kind", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly));
	}

	[Fact]
	public void Numeric_info_classifies_numeric_primitives_only()
	{
		Assert.True(NumericInfo.TryGet(PrimitiveKind.I32, out var i32));
		Assert.True(i32.IsInteger);
		Assert.True(i32.IsSigned);
		Assert.False(i32.IsUniversal);
		Assert.Equal(32, i32.Bits);

		Assert.True(NumericInfo.TryGet(PrimitiveKind.U64, out var u64));
		Assert.True(u64.IsInteger);
		Assert.False(u64.IsSigned);
		Assert.Equal(64, u64.Bits);

		Assert.True(NumericInfo.TryGet(PrimitiveKind.F32, out var f32));
		Assert.True(f32.IsFloat);
		Assert.Equal(32, f32.Bits);

		Assert.True(NumericInfo.TryGet(PrimitiveKind.Inum, out var inum));
		Assert.True(inum.IsUniversal);
		Assert.True(NumericInfo.TryGet(PrimitiveKind.Num, out var num));
		Assert.True(num.IsUniversal);

		Assert.False(NumericInfo.TryGet(PrimitiveKind.Bool, out _));
		Assert.False(NumericInfo.TryGet(PrimitiveKind.Str, out _));

		Assert.True(NumericInfo.IsNumeric(PrimitiveType.F64));
		Assert.True(NumericInfo.IsNumeric(PrimitiveType.Inum));
		Assert.False(NumericInfo.IsNumeric(PrimitiveType.Str));
		Assert.False(NumericInfo.IsNumeric(ErrorType.Instance));
	}
}
