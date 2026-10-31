using Kern.Bytecode;
using Kern.Vm;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Tests.Execution;

/// <summary>Сборка модулей для тестов VM и выполнение функций.</summary>
internal static class TestVm
{
	public static FunctionDef Function(string name, int[] code, int paramCount = 0, int localCount = 0)
	{
		return new FunctionDef
		{
			Name = name,
			ParamTypes = [.. Enumerable.Repeat(TypeSig.I32, paramCount)],
			LocalCount = Math.Max(localCount, paramCount),
			MaxStack = 64,
			Code = code,
		};
	}

	public static KbcModule Module(params FunctionDef[] functions)
	{
		KbcModule module = new() { Name = "test" };
		module.FunctionPool.AddRange(functions);
		return module;
	}

	public static KValue Run(int[] code, int paramCount = 0, int localCount = 0, params KValue[] args)
	{
		KbcModule module = Module(Function("main", code, paramCount, localCount));
		return new Interpreter(module).Run("main", args);
	}

	public static KValue RunWithConsts(KConst[] constants, int[] code)
	{
		KbcModule module = new() { Name = "test" };
		module.ConstPool.AddRange(constants);
		module.FunctionPool.Add(Function("main", code));
		return new Interpreter(module).Run("main", []);
	}

	public static KValue RunFunction(int functionIndex, params FunctionDef[] functions)
	{
		KbcModule module = Module(functions);
		return new Interpreter(module).Run(functionIndex, []);
	}
}
