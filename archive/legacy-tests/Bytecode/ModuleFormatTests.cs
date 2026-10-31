using Kern.Bytecode;
using Xunit;

namespace Kern.Tests.Bytecode;

public sealed class ModuleFormatTests
{
	[Fact]
	public void Reader_RejectsBadMagic()
	{
		byte[] bytes = [(byte)'N', (byte)'O', (byte)'P', (byte)'E', 0, 0];

		KbcFormatException error = Assert.Throws<KbcFormatException>(() => Read(bytes));

		Assert.Contains("сигнатура", error.Message);
	}

	[Fact]
	public void Reader_RejectsUnknownVersion()
	{
		byte[] bytes = BytecodeFixtures.MinimalBytes();
		bytes[4] = 99;
		bytes[5] = 0;

		KbcFormatException error = Assert.Throws<KbcFormatException>(() => Read(bytes));

		Assert.Contains("версия", error.Message);
		Assert.Contains("99", error.Message);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(3)]
	[InlineData(5)]
	[InlineData(10)]
	[InlineData(40)]
	public void Reader_RejectsTruncatedData(int length)
	{
		byte[] bytes = BytecodeFixtures.MinimalBytes();
		int cut = Math.Min(length, bytes.Length - 1);

		Assert.Throws<KbcFormatException>(() => Read(bytes[..cut]));
	}

	[Fact]
	public void Validator_RejectsJumpIntoOperand()
	{
		KbcModule module = BytecodeFixtures.Minimal();
		module.FunctionPool[0].Code = [(int)OpCode.Jmp, 1];

		Assert.Throws<KbcFormatException>(() => KbcModuleValidator.Validate(module));
		Assert.Throws<KbcFormatException>(() => KbcWriter.Write(module));
	}

	[Fact]
	public void Validator_RejectsOutOfRangeConstant()
	{
		KbcModule module = BytecodeFixtures.Minimal();
		module.FunctionPool[0].Code =
			[(int)OpCode.PushConst, 5, (int)OpCode.Ret];

		KbcFormatException error = Assert.Throws<KbcFormatException>(() => KbcModuleValidator.Validate(module));

		Assert.Contains("константы", error.Message);
	}

	[Fact]
	public void Validator_RejectsLocalOutsideFrame()
	{
		KbcModule module = BytecodeFixtures.Minimal();
		module.FunctionPool[0].Code =
			[(int)OpCode.LoadLocal, 3, (int)OpCode.Ret];

		Assert.Throws<KbcFormatException>(() => KbcModuleValidator.Validate(module));
	}

	[Fact]
	public void Validator_RejectsCallArityMismatch()
	{
		KbcModule module = BytecodeFixtures.Minimal();
		FunctionDef callee = BytecodeFixtures.Function("callee", (int)OpCode.PushVoid, (int)OpCode.Ret);
		module.FunctionPool.Add(callee);
		module.FunctionPool[0].Code =
			[(int)OpCode.PushI32, 1, (int)OpCode.Call, 1, 1, (int)OpCode.Ret];

		KbcFormatException error = Assert.Throws<KbcFormatException>(() => KbcModuleValidator.Validate(module));

		Assert.Contains("argc", error.Message);
	}

	[Fact]
	public void Validator_RejectsOutOfRangeStaticInit()
	{
		KbcModule module = BytecodeFixtures.Minimal();
		module.StaticInit = 1;

		KbcFormatException error = Assert.Throws<KbcFormatException>(() => KbcModuleValidator.Validate(module));

		Assert.Contains("<static-init>", error.Message);
		Assert.Throws<KbcFormatException>(() => KbcWriter.Write(module));
	}

	[Fact]
	public void Disassembler_FormatsModule()
	{
		KbcModule module = BytecodeFixtures.Minimal("demo");
		module.References.Add("kern.core");
		module.InternString("hello");
		module.InternConst(KConst.I64(42));
		module.InternTypeSig(TypeSig.I32);
		module.InternTypeSigList([TypeSig.I32, TypeSig.Str]);

		FunctionDef main = module.FunctionPool[0];
		main.Code =
			[(int)OpCode.PushI32, 1, (int)OpCode.PushI32, 2, (int)OpCode.Add, (int)OpCode.Ret];
		main.ReturnType = TypeSig.I32;
		main.MaxStack = 2;
		main.SourceFile = "main.kern";
		main.Attributes.Add(new AttributeUse("test", KConst.I64(1)));
		main.LineTable = [new LineEntry(0, 1), new LineEntry(4, 2)];
		module.Attributes.Add(new AttributeUse("module_attr"));

		string text = Disassembler.Disassemble(module);
		string expected = string.Join("\n",
		[
			"; KBC2 версия 2",
			".module \"demo\"",
			".refs 1",
			"  [0] \"kern.core\"",
			".strings 1",
			"  [0] \"hello\"",
			".consts 1",
			"  [0] i64 42",
			".typeSigs 2",
			"  [0] i32",
			"  [1] str",
			".typeSigLists 1",
			"  [0] [0, 1]",
			".types 0",
			".fields 0",
			".properties 0",
			".functions 1",
			"  #0 \"main\" params=0 locals=0 stack=2 owner=-1 -> i32 flags=None",
			"    source \"main.kern\"",
			"    attr test(i64 1)",
			"    0000  PushI32 1",
			"    0002  PushI32 2",
			"    0004  Add",
			"    0005  Ret",
			"    line 1 @ 0",
			"    line 2 @ 4",
			".entry 0",
			".static-init -1",
			".attrs 1",
			"  module_attr",
			"",
		]);

		Assert.Equal(expected, text);
	}

	[Fact]
	public void Disassembler_ShowsConstantAndCallComments()
	{
		KbcModule module = BytecodeFixtures.Minimal();
		module.InternConst(KConst.Str("привет"));
		FunctionDef callee = BytecodeFixtures.Function("callee", (int)OpCode.PushVoid, (int)OpCode.Ret);
		module.FunctionPool.Add(callee);
		module.FunctionPool[0].Code =
		[
			(int)OpCode.PushConst, 0,
			(int)OpCode.Call, 1, 0,
			(int)OpCode.Ret,
		];

		string text = Disassembler.Disassemble(module);

		Assert.Contains("PushConst 0  ; str \"привет\"", text);
		Assert.Contains("Call 1 0  ; -> \"callee\"", text);
	}

	[Fact]
	public void Disassembler_ShowsStaticInitIndex()
	{
		KbcModule module = BytecodeFixtures.Minimal();
		module.StaticInit = 0;

		string text = Disassembler.Disassemble(module);

		Assert.Contains(".entry 0\n.static-init 0\n", text, StringComparison.Ordinal);
	}

	private static void Read(byte[] bytes) => _ = KbcReader.Read(bytes);
}
