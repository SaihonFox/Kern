using Kern.Bytecode;
using Kern.Vm.Types;
using Xunit;

namespace Kern.Tests.Execution;

public sealed class TypeFactoryTests
{
	[Fact]
	public void Prim_IsInterned()
	{
		TypeFactory factory = new(new KbcModule { Name = "types" });

		Assert.Same(factory.Prim(PrimKind.I32), factory.Prim(PrimKind.I32));
		Assert.NotSame(factory.Prim(PrimKind.I32), factory.Prim(PrimKind.I64));
		Assert.Equal("i32", factory.Prim(PrimKind.I32).Canonical);
	}

	[Fact]
	public void Named_DistinguishesTypeArguments()
	{
		KbcModule module = ModuleWithBox();
		TypeFactory factory = new(module);
		TypeDef box = module.TypePool[0];

		KClassType intBox = factory.Named(box, factory.Prim(PrimKind.I32));
		KClassType strBox = factory.Named(box, factory.Prim(PrimKind.Str));

		Assert.NotSame(intBox, strBox);
		Assert.Same(intBox, factory.Named(box, factory.Prim(PrimKind.I32)));
		Assert.Equal("types.Box<i32>", intBox.Canonical);
		Assert.Equal("types.Box<str>", strBox.Canonical);
	}

	[Fact]
	public void BuiltinAndFunctionTypes_AreInterned()
	{
		TypeFactory factory = new(new KbcModule { Name = "types" });

		KType list = factory.Builtin(BuiltinGenericKind.List, factory.Prim(PrimKind.I32));
		Assert.Same(list, factory.Builtin(BuiltinGenericKind.List, factory.Prim(PrimKind.I32)));
		Assert.Equal("list<i32>", list.Canonical);

		KType function = factory.Func(factory.Prim(PrimKind.Bool), factory.Prim(PrimKind.I32));
		Assert.Same(function, factory.Func(factory.Prim(PrimKind.Bool), factory.Prim(PrimKind.I32)));
		Assert.Equal("fun(i32) -> bool", function.Canonical);

		KType nullable = factory.Nullable(factory.Prim(PrimKind.Str));
		Assert.Equal("str?", nullable.Canonical);
	}

	[Fact]
	public void FromSig_ResolvesUserAndBuiltinTypes()
	{
		KbcModule module = ModuleWithBox();
		TypeFactory factory = new(module);

		KType box = factory.FromSig(new TypeSig.NamedSig("types.Box", [TypeSig.I32]));
		KType list = factory.FromSig(new TypeSig.NamedSig("list", [TypeSig.Str]));
		KType function = factory.FromSig(
			new TypeSig.FuncSig([TypeSig.I32, TypeSig.F64], TypeSig.Bool));

		Assert.IsType<KClassType>(box);
		Assert.Equal("types.Box<i32>", box.Canonical);
		Assert.IsType<KBuiltinGenericType>(list);
		Assert.Equal("list<str>", list.Canonical);
		Assert.IsType<KFuncType>(function);
		Assert.Equal("fun(i32, f64) -> bool", function.Canonical);
	}

	[Fact]
	public void FromSig_RejectsTypeParameterAndUnknownType()
	{
		TypeFactory factory = new(ModuleWithBox());

		Assert.Throws<InvalidOperationException>(
			() => factory.FromSig(new TypeSig.ParamSig(0, 0, "T")));
		Assert.Throws<InvalidOperationException>(
			() => factory.FromSig(new TypeSig.NamedSig("types.Missing", [])));
		Assert.Throws<InvalidOperationException>(
			() => factory.FromSig(new TypeSig.NamedSig("dict", [TypeSig.I32])));
	}

	[Fact]
	public void Interning_IsThreadSafe()
	{
		TypeFactory factory = new(new KbcModule { Name = "types" });
		KType[] results = new KType[64];

		Parallel.For(0, results.Length, i => results[i] = factory.Prim(PrimKind.F64));

		Assert.All(results, type => Assert.Same(results[0], type));
	}

	private static KbcModule ModuleWithBox()
	{
		KbcModule module = new() { Name = "types" };
		module.TypePool.Add(new TypeDef
		{
			Name = "Box",
			FullName = "types.Box",
			Kind = TypeDefKind.Class,
			GenericParams = ["T"],
		});
		return module;
	}
}
