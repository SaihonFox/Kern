using Kern.Bytecode;
using Kern.Vm.Types;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Values;

public sealed class UnitValueEqualityTests
{
	[Fact]
	public void SameUnitTypeAndFields_AreEqualWithSameHash()
	{
		TypeFactory factory = NewFactory();
		KClassType point = NamedType(factory, "Point", TypeDefKind.Unit);

		KValue a = KValue.Obj(new KObject(point, [KValue.I32(1), KValue.I32(2)]));
		KValue b = KValue.Obj(new KObject(point, [KValue.I32(1), KValue.I32(2)]));
		KValue c = KValue.Obj(new KObject(point, [KValue.I32(1), KValue.I32(3)]));

		Assert.True(a.Equals(b));
		Assert.Equal(a.GetHashCode(), b.GetHashCode());
		Assert.False(a.Equals(c));
	}

	[Fact]
	public void AreEqual_IsStructuralForUnits_AndReferenceForClasses()
	{
		TypeFactory factory = NewFactory();
		KClassType point = NamedType(factory, "Point", TypeDefKind.Unit);
		KClassType holder = NamedType(factory, "Holder", TypeDefKind.Class);

		KValue unitA = KValue.Obj(new KObject(point, [KValue.I32(7)]));
		KValue unitB = KValue.Obj(new KObject(point, [KValue.I32(7)]));
		Assert.True(KValueMath.AreEqual(unitA, unitB));

		KValue classA = KValue.Obj(new KObject(holder, [KValue.I32(7)]));
		KValue classB = KValue.Obj(new KObject(holder, [KValue.I32(7)]));
		Assert.False(KValueMath.AreEqual(classA, classB));
		Assert.True(KValueMath.AreEqual(classA, classA));

		// межвидовая числовая семантика == не изменилась
		Assert.True(KValueMath.AreEqual(KValue.I32(1), KValue.U8(1)));
	}

	[Fact]
	public void NestedUnitField_IsComparedRecursively()
	{
		TypeFactory factory = NewFactory();
		KClassType point = NamedType(factory, "Point", TypeDefKind.Unit);
		KClassType segment = NamedType(factory, "Segment", TypeDefKind.Unit);

		KObject inner = new(point, [KValue.I32(1), KValue.I32(2)]);
		KObject innerEqual = new(point, [KValue.I32(1), KValue.I32(2)]);
		KObject innerDifferent = new(point, [KValue.I32(1), KValue.I32(3)]);

		KValue outer = KValue.Obj(new KObject(segment, [KValue.Obj(inner)]));
		KValue outerEqual = KValue.Obj(new KObject(segment, [KValue.Obj(innerEqual)]));
		KValue outerDifferent = KValue.Obj(new KObject(segment, [KValue.Obj(innerDifferent)]));

		Assert.True(outer.Equals(outerEqual));
		Assert.Equal(outer.GetHashCode(), outerEqual.GetHashCode());
		Assert.False(outer.Equals(outerDifferent));
	}

	[Fact]
	public void DifferentUnitTypes_WithSameFields_AreNotEqual()
	{
		TypeFactory factory = NewFactory();
		KClassType point = NamedType(factory, "Point", TypeDefKind.Unit);
		KClassType vector = NamedType(factory, "Vector", TypeDefKind.Unit);

		KValue p = KValue.Obj(new KObject(point, [KValue.I32(1), KValue.I32(2)]));
		KValue v = KValue.Obj(new KObject(vector, [KValue.I32(1), KValue.I32(2)]));

		Assert.False(p.Equals(v));
		Assert.False(KValueMath.AreEqual(p, v));
	}

	[Fact]
	public void KDict_KeyedByUnit_FindsStructurallyEqualKey()
	{
		TypeFactory factory = NewFactory();
		KClassType point = NamedType(factory, "Point", TypeDefKind.Unit);
		KDict dict = new();

		KObject a = new(point, [KValue.I32(1), KValue.Str("x")]);
		KObject b = new(point, [KValue.I32(1), KValue.Str("x")]);

		dict.Set(KValue.Obj(a), KValue.Str("first"));
		Assert.True(dict.TryGet(KValue.Obj(b), out KValue found));
		Assert.Equal("first", found.AsStr);

		dict.Set(KValue.Obj(b), KValue.Str("second"));
		Assert.Equal(1, dict.Count);
		Assert.True(dict.TryGet(KValue.Obj(a), out KValue updated));
		Assert.Equal("second", updated.AsStr);
	}

	private static TypeFactory NewFactory() => new(new KbcModule { Name = "test" });

	private static KClassType NamedType(TypeFactory factory, string name, TypeDefKind kind) =>
		factory.Named(new TypeDef
		{
			Name = name,
			FullName = $"test.{name}",
			Kind = kind,
		});
}
