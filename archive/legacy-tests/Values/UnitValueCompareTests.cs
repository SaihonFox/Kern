using System.Runtime.CompilerServices;
using Kern.Bytecode;
using Kern.Vm.Types;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Values;

public sealed class UnitValueCompareTests
{
	[Fact]
	public void StructuralHash64_IsStructuralForUnits_AndIdentityForClasses()
	{
		TypeFactory factory = NewFactory();
		KClassType point = NamedType(factory, "Point", TypeDefKind.Unit);
		KClassType segment = NamedType(factory, "Segment", TypeDefKind.Unit);
		KClassType holder = NamedType(factory, "Holder", TypeDefKind.Class);

		KValue a = KValue.Obj(new KObject(point, [KValue.I32(1), KValue.I32(2)]));
		KValue b = KValue.Obj(new KObject(point, [KValue.I32(1), KValue.I32(2)]));
		KValue c = KValue.Obj(new KObject(point, [KValue.I32(1), KValue.I32(3)]));

		Assert.Equal(KValueMath.StructuralHash64(a), KValueMath.StructuralHash64(b));
		Assert.NotEqual(KValueMath.StructuralHash64(a), KValueMath.StructuralHash64(c));

		KValue nestedA = KValue.Obj(new KObject(segment, [Unit(point, 1, 2)]));
		KValue nestedB = KValue.Obj(new KObject(segment, [Unit(point, 1, 2)]));
		Assert.Equal(KValueMath.StructuralHash64(nestedA), KValueMath.StructuralHash64(nestedB));

		KObject instance = new(holder, [KValue.I32(1)]);
		Assert.Equal(
			unchecked((ulong)RuntimeHelpers.GetHashCode(instance)),
			KValueMath.StructuralHash64(KValue.Obj(instance)));
	}

	[Fact]
	public void StructuralCompare_Units_AreLexicographicByFields()
	{
		TypeFactory factory = NewFactory();
		KClassType point = NamedType(factory, "Point", TypeDefKind.Unit);
		KClassType segment = NamedType(factory, "Segment", TypeDefKind.Unit);

		KValue first = Unit(point, 1, 2);
		KValue second = Unit(point, 1, 3);
		KValue third = Unit(point, 2, 0);
		KValue equal = Unit(point, 1, 2);

		Assert.True(KValueMath.StructuralCompare(first, second) < 0);
		Assert.True(KValueMath.StructuralCompare(second, third) < 0);
		Assert.True(KValueMath.StructuralCompare(first, third) < 0);
		Assert.Equal(0, KValueMath.StructuralCompare(first, equal));
		Assert.True(KValueMath.StructuralCompare(second, first) > 0);

		KValue nestedFirst = KValue.Obj(new KObject(segment, [Unit(point, 1, 2)]));
		KValue nestedSecond = KValue.Obj(new KObject(segment, [Unit(point, 1, 3)]));
		KValue nestedEqual = KValue.Obj(new KObject(segment, [Unit(point, 1, 2)]));
		Assert.True(KValueMath.StructuralCompare(nestedFirst, nestedSecond) < 0);
		Assert.Equal(0, KValueMath.StructuralCompare(nestedFirst, nestedEqual));
	}

	[Fact]
	public void StructuralCompare_Primitives_UseKindOrder()
	{
		Assert.True(KValueMath.StructuralCompare(KValue.I32(1), KValue.I32(2)) < 0);
		Assert.Equal(0, KValueMath.StructuralCompare(KValue.I32(1), KValue.I32(1)));
		Assert.Equal(0, KValueMath.StructuralCompare(KValue.I32(1), KValue.U8(1)));
		Assert.True(KValueMath.StructuralCompare(KValue.I32(-1), KValue.U8(1)) < 0);

		Assert.True(KValueMath.StructuralCompare(KValue.Str("a"), KValue.Str("b")) < 0);
		Assert.Equal(0, KValueMath.StructuralCompare(KValue.Str("a"), KValue.Str("a")));

		Assert.True(KValueMath.StructuralCompare(KValue.False, KValue.True) < 0);
		Assert.Equal(0, KValueMath.StructuralCompare(KValue.True, KValue.True));

		Assert.True(KValueMath.StructuralCompare(KValue.Char('a'), KValue.Char('b')) < 0);
		Assert.Equal(0, KValueMath.StructuralCompare(KValue.Char('a'), KValue.Char('a')));

		Assert.Equal(0, KValueMath.StructuralCompare(KValue.Nil, KValue.Nil));
		Assert.Equal(0, KValueMath.StructuralCompare(KValue.Void, KValue.Void));
		Assert.Equal(0, KValueMath.StructuralCompare(KValue.None, KValue.None));
	}

	[Fact]
	public void StructuralCompare_ClassRefs_UseIdentityHashOrder()
	{
		TypeFactory factory = NewFactory();
		KClassType holder = NamedType(factory, "Holder", TypeDefKind.Class);

		KObject first = new(holder, [KValue.I32(1)]);
		KObject second = new(holder, [KValue.I32(2)]);
		KValue firstValue = KValue.Obj(first);
		KValue secondValue = KValue.Obj(second);

		Assert.Equal(0, KValueMath.StructuralCompare(firstValue, firstValue));
		Assert.Equal(0, KValueMath.StructuralCompare(firstValue, KValue.Obj(first)));

		int expected = Math.Sign(
			RuntimeHelpers.GetHashCode(first).CompareTo(RuntimeHelpers.GetHashCode(second)));
		Assert.Equal(expected, KValueMath.StructuralCompare(firstValue, secondValue));
		Assert.Equal(-expected, KValueMath.StructuralCompare(secondValue, firstValue));
	}

	private static KValue Unit(KClassType type, params int[] fields) =>
		KValue.Obj(new KObject(type, [.. fields.Select(field => KValue.I32(field))]));

	private static TypeFactory NewFactory() => new(new KbcModule { Name = "test" });

	private static KClassType NamedType(TypeFactory factory, string name, TypeDefKind kind) =>
		factory.Named(new TypeDef
		{
			Name = name,
			FullName = $"test.{name}",
			Kind = kind,
		});
}
