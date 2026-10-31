using Kern.Bytecode;
using Xunit;

namespace Kern.Tests.Bytecode;

public sealed class TypeSigTests
{
	[Fact]
	public void CanonicalText_CoversAllForms()
	{
		Assert.Equal("i32", TypeSig.I32.Canonical);
		Assert.Equal("list<i32>", new TypeSig.NamedSig("list", [TypeSig.I32]).Canonical);
		Assert.Equal("dict<str, i32>",
			new TypeSig.NamedSig("dict", [TypeSig.Str, TypeSig.I32]).Canonical);
		Assert.Equal("fun(i32, str) -> bool",
			new TypeSig.FuncSig([TypeSig.I32, TypeSig.Str], TypeSig.Bool).Canonical);
		Assert.Equal("(i32, str)", new TypeSig.TupleSig([TypeSig.I32, TypeSig.Str], null).Canonical);
		Assert.Equal("str?", new TypeSig.NullableSig(TypeSig.Str).Canonical);
		Assert.Equal("[i32, str]", new TypeSig.UnionSig([TypeSig.I32, TypeSig.Str]).Canonical);
		Assert.Equal("$0:T", new TypeSig.ParamSig(0, 0, "T").Canonical);
		Assert.Equal("$1:2", new TypeSig.ParamSig(1, 2, "").Canonical);
	}

	[Fact]
	public void Equality_IsStructural()
	{
		TypeSig left = new TypeSig.NamedSig("demo.Box", [TypeSig.I32, TypeSig.Str]);
		TypeSig right = new TypeSig.NamedSig("demo.Box", [TypeSig.I32, TypeSig.Str]);
		TypeSig other = new TypeSig.NamedSig("demo.Box", [TypeSig.Str, TypeSig.I32]);

		Assert.Equal(left, right);
		Assert.Equal(left.GetHashCode(), right.GetHashCode());
		Assert.NotEqual(left, other);
	}

	[Fact]
	public void TupleEquality_IgnoresElementNames()
	{
		TypeSig named = new TypeSig.TupleSig([TypeSig.I32, TypeSig.Str], ["x", "y"]);
		TypeSig plain = new TypeSig.TupleSig([TypeSig.I32, TypeSig.Str], null);

		Assert.Equal(named, plain);
		Assert.Equal(named.GetHashCode(), plain.GetHashCode());
	}

	[Fact]
	public void PrimNames_AreStable()
	{
		Assert.Equal("i32", PrimKind.I32.ToKernName());
		Assert.Equal("u64", PrimKind.U64.ToKernName());
		Assert.Equal("never", PrimKind.Never.ToKernName());
		Assert.True(PrimNames.IsDefined(PrimKind.F64));
		Assert.False(PrimNames.IsDefined((PrimKind)200));
	}
}
