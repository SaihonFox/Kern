using Kern.Bytecode;
using Kern.Tests.Execution;
using Xunit;

namespace Kern.Tests.Bytecode;

public sealed class ModuleRoundTripTests
{
	[Fact]
	public void RoundTrip_FullModule_ProducesIdenticalBytes()
	{
		KbcModule original = BuildFullModule();

		byte[] first = KbcWriter.Write(original);
		KbcModule restored = KbcReader.Read(first);
		byte[] second = KbcWriter.Write(restored);

		Assert.Equal(first, second);
		Assert.Equal(original.Name, restored.Name);
		Assert.Equal(original.References, restored.References);
		Assert.Equal(original.StringPool, restored.StringPool);
		Assert.Equal(original.ConstPool, restored.ConstPool);
		Assert.Equal(original.TypeSigPool, restored.TypeSigPool);
		Assert.Equal(original.EntryPoint, restored.EntryPoint);
		Assert.Equal(original.StaticInit, restored.StaticInit);
		Assert.Equal(1, restored.StaticInit);

		Assert.Equal(original.FunctionPool.Count, restored.FunctionPool.Count);
		FunctionDef ctor = restored.FunctionPool[0];
		Assert.Equal(original.FunctionPool[0].Code, ctor.Code);
		Assert.Equal(original.FunctionPool[0].LocalCount, ctor.LocalCount);
		Assert.Equal(original.FunctionPool[0].ParamTypes, ctor.ParamTypes);
		Assert.Equal(original.FunctionPool[0].Attributes[0].ToString(), ctor.Attributes[0].ToString());

		FunctionDef getter = restored.FunctionPool[1];
		Assert.Equal(original.FunctionPool[1].LineTable, getter.LineTable);
		Assert.Equal("box.kern", getter.SourceFile);
		Assert.Equal(MemberFlags.Getter, getter.Flags);

		TypeDef type = restored.TypePool[0];
		Assert.Equal("demo.Box", type.FullName);
		Assert.Equal(TypeDefKind.Class, type.Kind);
		Assert.Equal(original.TypePool[0].GenericParams, type.GenericParams);
		Assert.Equal("value", type.Fields[0].Name);
		Assert.Equal("len", type.Properties[0].Name);
		Assert.Equal(1, type.Properties[0].Getter);
		Assert.Equal(0, type.Properties[0].BackingField);
		Assert.Equal(1, type.Methods[0]);
		Assert.Equal(0, type.Ctors[0]);
		Assert.Equal(original.TypePool[0].Attributes[0].ToString(), type.Attributes[0].ToString());
	}

	[Fact]
	public void RoundTrip_AllConstantKinds()
	{
		KConst[] values =
		[
			KConst.Nil,
			KConst.None,
			KConst.Void,
			KConst.Bool(true),
			KConst.Bool(false),
			KConst.I64(long.MinValue),
			KConst.I64(0),
			KConst.F64(-0.0),
			KConst.F64(double.NaN),
			KConst.Char('Ж'),
			KConst.Str(""),
			KConst.Str("привет\n\"x\"\\"),
		];

		KbcModule module = BytecodeFixtures.Minimal();
		foreach (KConst value in values) module.InternConst(value);

		KbcModule restored = KbcReader.Read(KbcWriter.Write(module));

		Assert.Equal(values, restored.ConstPool);
	}

	[Fact]
	public void RoundTrip_TypeSigListsAndEntryPoint()
	{
		KbcModule module = BytecodeFixtures.Minimal();
		module.InternTypeSigList([TypeSig.I32, TypeSig.Str]);
		module.InternTypeSigList([TypeSig.I32]);
		module.EntryPoint = 0;

		KbcModule restored = KbcReader.Read(KbcWriter.Write(module));

		Assert.Equal(2, restored.TypeSigListPool.Count);
		Assert.Equal(module.TypeSigListPool[0], restored.TypeSigListPool[0]);
		Assert.Equal(0, restored.EntryPoint);
	}

	[Fact]
	public void RoundTrip_DerivedProtocolOpcodes()
	{
		// B8a/B8c: все три опкода без операндов; проверяем валидатор и обратный round-trip кода.
		KbcModule module = BytecodeFixtures.Minimal();
		module.FunctionPool.Add(BytecodeFixtures.Function(
			"derived",
			(int)OpCode.PushI32, 1,
			(int)OpCode.ValueHashFields,
			(int)OpCode.PushI32, 2,
			(int)OpCode.ValueCompareFields,
			(int)OpCode.PushI32, 3,
			(int)OpCode.CloneObject,
			(int)OpCode.Ret));

		KbcModuleValidator.Validate(module);
		KbcModule restored = KbcReader.Read(KbcWriter.Write(module));

		int derived = restored.FunctionPool.Count - 1;
		Assert.Equal("derived", restored.FunctionPool[derived].Name);
		Assert.Equal(module.FunctionPool[derived].Code, restored.FunctionPool[derived].Code);
		Assert.Contains((int)OpCode.ValueHashFields, restored.FunctionPool[derived].Code);
		Assert.Contains((int)OpCode.ValueCompareFields, restored.FunctionPool[derived].Code);
		Assert.Contains((int)OpCode.CloneObject, restored.FunctionPool[derived].Code);
	}

	[Fact]
	public void RoundTrip_StaticInitDefaultsToMinusOneAndIsPreserved()
	{
		KbcModule module = BytecodeFixtures.Minimal();
		Assert.Equal(-1, module.StaticInit);

		KbcModule restoredDefault = KbcReader.Read(KbcWriter.Write(module));
		Assert.Equal(-1, restoredDefault.StaticInit);

		module.StaticInit = 0;
		KbcModule restored = KbcReader.Read(KbcWriter.Write(module));
		Assert.Equal(0, restored.StaticInit);
	}

	[Fact]
	public void Intern_DoesNotCreateDuplicates()
	{
		KbcModule module = BytecodeFixtures.Minimal();

		Assert.Equal(module.InternString("x"), module.InternString("x"));
		Assert.Equal(module.InternConst(KConst.F64(1.5)), module.InternConst(KConst.F64(1.5)));
		Assert.Equal(module.InternTypeSig(TypeSig.I32), module.InternTypeSig(TypeSig.I32));
		Assert.Equal(module.InternTypeSigList([TypeSig.I32]), module.InternTypeSigList([TypeSig.I32]));

		Assert.Single(module.StringPool);
		Assert.Single(module.ConstPool);
		Assert.Single(module.TypeSigPool);
		Assert.Single(module.TypeSigListPool);
	}

	[Fact]
	public void FindFunction_MatchesNameAndArity()
	{
		KbcModule module = BytecodeFixtures.Minimal();
		module.FunctionPool.Add(TestVm.Function("f", [(int)OpCode.PushVoid, (int)OpCode.Ret], paramCount: 0));
		module.FunctionPool.Add(TestVm.Function("f", [(int)OpCode.PushVoid, (int)OpCode.Ret], paramCount: 1));

		Assert.True(module.TryFindFunction("f", 1, out int one));
		Assert.Equal(2, one);
		Assert.False(module.TryFindFunction("f", 2, out _));
		Assert.False(module.TryFindFunction("g", 0, out _));
	}

	private static KbcModule BuildFullModule()
	{
		KbcModule module = new() { Name = "demo" };
		module.References.Add("kern.core");
		module.InternString("hello");
		module.InternConst(KConst.Nil);
		module.InternConst(KConst.None);
		module.InternConst(KConst.Void);
		module.InternConst(KConst.Bool(true));
		module.InternConst(KConst.I64(-42));
		module.InternConst(KConst.F64(1.5));
		module.InternConst(KConst.Char('Ж'));
		module.InternConst(KConst.Str("привет\n\"x\""));

		module.InternTypeSig(TypeSig.I32);
		module.InternTypeSig(new TypeSig.NamedSig("demo.Box", [TypeSig.I32]));
		module.InternTypeSig(new TypeSig.ParamSig(1, 0, "T"));
		module.InternTypeSig(new TypeSig.FuncSig([TypeSig.I32, TypeSig.Str], TypeSig.Bool));
		module.InternTypeSig(new TypeSig.TupleSig([TypeSig.I32, TypeSig.Str], ["x", "y"]));
		module.InternTypeSig(new TypeSig.NullableSig(TypeSig.Str));
		module.InternTypeSig(new TypeSig.UnionSig([TypeSig.I32, TypeSig.Str]));
		module.InternTypeSigList([TypeSig.I32, TypeSig.Str]);

		TypeDef box = new()
		{
			Name = "Box",
			FullName = "demo.Box",
			Kind = TypeDefKind.Class,
			Flags = MemberFlags.Pub,
			GenericParams = ["T"],
			Parents = [TypeSig.Object],
		};
		FieldDef value = new()
		{
			Name = "value",
			Type = new TypeSig.ParamSig(0, 0, "T"),
			Flags = MemberFlags.Priv,
			Slot = 0,
			Attributes = [new AttributeUse("inject")],
		};
		box.Fields.Add(value);
		PropertyDef length = new()
		{
			Name = "len",
			Type = TypeSig.I32,
			Getter = 1,
			BackingField = 0,
			Attributes = [new AttributeUse("obsolete", KConst.Str("нет"))],
		};
		box.Properties.Add(length);
		box.Methods.Add(1);
		box.Ctors.Add(0);
		box.Attributes.Add(new AttributeUse("unit_test", KConst.Bool(true), KConst.I64(7)));
		module.TypePool.Add(box);

		FunctionDef ctor = new()
		{
			Name = ".ctor",
			OwnerType = 0,
			Flags = MemberFlags.Ctor,
			ParamTypes = [TypeSig.I32],
			ReturnType = TypeSig.Void,
			LocalCount = 2,
			MaxStack = 1,
			Code = [(int)OpCode.LoadLocal, 1, (int)OpCode.Ret],
			Attributes = [new AttributeUse("generated")],
		};
		FunctionDef getter = new()
		{
			Name = ".get_len",
			OwnerType = 0,
			Flags = MemberFlags.Getter,
			ParamTypes = [],
			ReturnType = TypeSig.I32,
			LocalCount = 1,
			MaxStack = 1,
			Code = [(int)OpCode.PushI32, 0, (int)OpCode.Ret],
			LineTable = [new LineEntry(0, 10), new LineEntry(2, 11)],
			SourceFile = "box.kern",
			Attributes = [new AttributeUse("test", KConst.None)],
		};
		module.FunctionPool.Add(ctor);
		module.FunctionPool.Add(getter);

		module.FieldPool.Add(value);
		module.PropertyPool.Add(length);
		module.EntryPoint = 0;
		module.StaticInit = 1;
		module.Attributes.Add(new AttributeUse("module_attr", KConst.Str("x")));
		return module;
	}
}
