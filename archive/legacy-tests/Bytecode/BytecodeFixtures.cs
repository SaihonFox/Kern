using Kern.Bytecode;

namespace Kern.Tests.Bytecode;

/// <summary>Общие минимальные модули для тестов формата.</summary>
internal static class BytecodeFixtures
{
	public static FunctionDef Function(string name, params int[] code) => new()
	{
		Name = name,
		Code = code,
		ReturnType = TypeSig.Void,
		MaxStack = 8,
	};

	public static KbcModule Minimal(string name = "test")
	{
		KbcModule module = new() { Name = name };
		module.FunctionPool.Add(Function("main", (int)OpCode.PushVoid, (int)OpCode.Ret));
		module.EntryPoint = 0;
		return module;
	}

	public static byte[] MinimalBytes(string name = "test") => KbcWriter.Write(Minimal(name));
}
