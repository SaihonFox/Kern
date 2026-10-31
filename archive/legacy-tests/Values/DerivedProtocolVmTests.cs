using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Values;

/// <summary>
/// B8a: производные <c>hash</c>/<c>compare</c> по полям любого объекта
/// (<see cref="KValueMath.FieldHash64"/>/<see cref="KValueMath.FieldCompare"/>).
/// Отличие от B6b1: class-объект тоже хэшируется и сравнивается по полям,
/// но вложенные class-ссылки внутри полей остаются идентичностью.
/// B8c: <see cref="KObject.CloneShallow"/> — неглубокая копия для <c>#[derive(clone)]</c>:
/// unit-поля копируются, class-ссылки остаются общими, признак значения сохраняется.
/// </summary>
public sealed class DerivedProtocolVmTests
{
	[Fact]
	public void FieldHash64_ClassesHashByFields_UnitsMatchStructuralHash()
	{
		TypeFactory factory = NewFactory();
		KClassType holder = NamedType(factory, "Holder", TypeDefKind.Class);
		KClassType point = NamedType(factory, "Point", TypeDefKind.Unit);

		KValue first = KValue.Obj(new KObject(holder, [KValue.I32(1), KValue.Str("a")]));
		KValue equal = KValue.Obj(new KObject(holder, [KValue.I32(1), KValue.Str("a")]));
		KValue different = KValue.Obj(new KObject(holder, [KValue.I32(1), KValue.Str("b")]));

		Assert.Equal(KValueMath.FieldHash64(first), KValueMath.FieldHash64(equal));
		Assert.NotEqual(KValueMath.FieldHash64(first), KValueMath.FieldHash64(different));

		// unit хэшируется ровно как раньше: полевая часть общая.
		KValue unitFirst = Unit(point, 1, 2);
		KValue unitEqual = Unit(point, 1, 2);
		Assert.Equal(KValueMath.StructuralHash64(unitFirst), KValueMath.FieldHash64(unitFirst));
		Assert.Equal(KValueMath.FieldHash64(unitFirst), KValueMath.FieldHash64(unitEqual));

		// Не-объекты не меняются.
		Assert.Equal(KValueMath.StructuralHash64(KValue.I32(5)), KValueMath.FieldHash64(KValue.I32(5)));
	}

	[Fact]
	public void FieldHash64_NestedClassRefsStayIdentityHashed()
	{
		TypeFactory factory = NewFactory();
		KClassType holder = NamedType(factory, "Holder", TypeDefKind.Class);
		KClassType part = NamedType(factory, "Part", TypeDefKind.Class);

		KValue outerFirst = KValue.Obj(new KObject(holder, [KValue.Obj(new KObject(part, [KValue.I32(7)]))]));
		KValue outerSecond = KValue.Obj(new KObject(holder, [KValue.Obj(new KObject(part, [KValue.I32(7)]))]));

		// Разные экземпляры вложенного class с равными полями — разные хэши.
		Assert.NotEqual(KValueMath.FieldHash64(outerFirst), KValueMath.FieldHash64(outerSecond));

		// Одна и та же вложенная ссылка — одинаковый хэш.
		KObject shared = new(part, [KValue.I32(7)]);
		KValue withSharedFirst = KValue.Obj(new KObject(holder, [KValue.Obj(shared)]));
		KValue withSharedSecond = KValue.Obj(new KObject(holder, [KValue.Obj(shared)]));
		Assert.Equal(KValueMath.FieldHash64(withSharedFirst), KValueMath.FieldHash64(withSharedSecond));
	}

	[Fact]
	public void FieldCompare_ClassesCompareTypeThenFieldsThenCount()
	{
		TypeFactory factory = NewFactory();
		KClassType holder = NamedType(factory, "Holder", TypeDefKind.Class);

		KValue first = KValue.Obj(new KObject(holder, [KValue.I32(1), KValue.Str("a")]));
		KValue second = KValue.Obj(new KObject(holder, [KValue.I32(1), KValue.Str("b")]));
		KValue third = KValue.Obj(new KObject(holder, [KValue.I32(2), KValue.Str("a")]));
		KValue equal = KValue.Obj(new KObject(holder, [KValue.I32(1), KValue.Str("a")]));

		Assert.True(KValueMath.FieldCompare(first, second) < 0);
		Assert.True(KValueMath.FieldCompare(second, third) < 0);
		Assert.True(KValueMath.FieldCompare(first, third) < 0);
		Assert.Equal(0, KValueMath.FieldCompare(first, equal));
		Assert.True(KValueMath.FieldCompare(second, first) > 0);

		// Равный префикс — меньшее число полей меньше.
		KValue shorter = KValue.Obj(new KObject(holder, [KValue.I32(1)]));
		KValue longer = KValue.Obj(new KObject(holder, [KValue.I32(1), KValue.I32(0)]));
		Assert.True(KValueMath.FieldCompare(shorter, longer) < 0);
		Assert.True(KValueMath.FieldCompare(longer, shorter) > 0);

		// Сначала канонический текст reified-типа, даже если поля "меньше" у другого типа.
		KClassType alpha = NamedType(factory, "Alpha", TypeDefKind.Class);
		KClassType zeta = NamedType(factory, "Zeta", TypeDefKind.Class);
		KValue alphaValue = KValue.Obj(new KObject(alpha, [KValue.I32(9)]));
		KValue zetaValue = KValue.Obj(new KObject(zeta, [KValue.I32(0)]));
		Assert.True(KValueMath.FieldCompare(alphaValue, zetaValue) < 0);
		Assert.True(KValueMath.FieldCompare(zetaValue, alphaValue) > 0);
	}

	[Fact]
	public void FieldCompare_UnitsAndPrimitivesMatchStructuralCompare()
	{
		TypeFactory factory = NewFactory();
		KClassType point = NamedType(factory, "Point", TypeDefKind.Unit);

		KValue first = Unit(point, 1, 2);
		KValue second = Unit(point, 1, 3);
		KValue equal = Unit(point, 1, 2);

		Assert.Equal(KValueMath.StructuralCompare(first, second), KValueMath.FieldCompare(first, second));
		Assert.Equal(KValueMath.StructuralCompare(second, first), KValueMath.FieldCompare(second, first));
		Assert.Equal(KValueMath.StructuralCompare(first, equal), KValueMath.FieldCompare(first, equal));
		Assert.Equal(0, KValueMath.FieldCompare(first, equal));

		Assert.Equal(
			KValueMath.StructuralCompare(KValue.I32(1), KValue.I32(2)),
			KValueMath.FieldCompare(KValue.I32(1), KValue.I32(2)));
		Assert.Equal(
			KValueMath.StructuralCompare(KValue.Str("a"), KValue.I32(1)),
			KValueMath.FieldCompare(KValue.Str("a"), KValue.I32(1)));
	}

	[Fact]
	public void CloneShallow_CopiesFieldsIntoADistinctInstance()
	{
		TypeFactory factory = NewFactory();
		KClassType holder = NamedType(factory, "Holder", TypeDefKind.Class);

		KObject original = new(holder, [KValue.I32(1), KValue.Str("a")]);
		KObject clone = original.CloneShallow();

		Assert.NotSame(original, clone);
		Assert.Same(original.Type, clone.Type);
		Assert.False(clone.IsUnit);
		Assert.Equal(original.FieldCount, clone.FieldCount);
		Assert.Equal(KValue.I32(1), clone.GetField(0));
		Assert.Equal(KValue.Str("a"), clone.GetField(1));
	}

	[Fact]
	public void CloneShallow_CopiesUnitFieldsAndSharesClassReferences()
	{
		TypeFactory factory = NewFactory();
		KClassType holder = NamedType(factory, "Holder", TypeDefKind.Class);
		KClassType point = NamedType(factory, "Point", TypeDefKind.Unit);
		KClassType part = NamedType(factory, "Part", TypeDefKind.Class);

		KObject unitField = new(point, [KValue.I32(7)]);
		KObject classField = new(part, [KValue.I32(9)]);
		KObject original = new(holder, [KValue.Obj(unitField), KValue.Obj(classField)]);
		KObject clone = original.CloneShallow();

		KObject copiedUnit = Assert.IsType<KObject>(clone.GetField(0).Ref);
		Assert.NotSame(unitField, copiedUnit);
		Assert.True(copiedUnit.IsUnit);
		copiedUnit.SetField(0, KValue.I32(42));
		Assert.Equal(KValue.I32(7), unitField.GetField(0));

		KObject sharedClass = Assert.IsType<KObject>(clone.GetField(1).Ref);
		Assert.Same(classField, sharedClass);
	}

	[Fact]
	public void CloneShallow_KeepsImmutableUnitSealed()
	{
		TypeFactory factory = NewFactory();
		KClassType frozen = NamedType(factory, "Frozen", TypeDefKind.ImmutableUnit);

		KObject original = new(frozen, [KValue.I32(1)]);
		KObject clone = original.CloneShallow();

		Assert.NotSame(original, clone);
		Assert.True(clone.IsUnit);
		Assert.True(clone.IsImmutable);
		Assert.Throws<KernPanic>(() => clone.SetField(0, KValue.I32(2)));
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
