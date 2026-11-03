using Kern.Binding.Resolution;
using Kern.Bytecode;
using Kern.Compiler.Optimization;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Оптимизатор: точный <c>MaxStack</c>, инлайнинг <c>#[inline]</c> и удаление мёртвых
/// приватных функций (spec §7/§16).
/// </summary>
public sealed class OptimizerTests
{
	[Fact]
	public void MaxStack_is_exact_for_a_simple_expression()
	{
		KbcModule module = StdPipeline.Compile("fun add(i32 a, i32 b) -> i32 a + b + 1").Module;
		Assert.True(module.TryFindFunction("add", 2, out int index));
		Assert.Equal(2, module.FunctionPool[index].MaxStack);
	}

	[Fact]
	public void Inline_attribute_removes_the_call_and_dead_function_elimination_drops_it()
	{
		KbcModule module = StdPipeline.Compile(
			"""
			#[inline] fun double(i32 value) -> i32 value * 2

			fun main() -> i32 double(21)
			""").Module;
		Assert.True(module.TryFindFunction("double", 1, out int doubleIndex));
		Assert.True(module.TryFindFunction("main", 0, out int mainIndex));

		Inliner.Inline(module);
		Assert.DoesNotContain(
			Calls(module.FunctionPool[mainIndex].Code),
			target => target == doubleIndex);

		DeadFunctionEliminator.Eliminate(module);
		Assert.False(module.TryFindFunction("double", 1, out _));
		Assert.True(module.TryFindFunction("main", 0, out mainIndex));
		StackAnalyzer.Compute(module);
		Assert.Equal(KValue.I32(42), Run(module, "main"));
	}

	[Fact]
	public void Dead_private_function_is_removed_but_used_and_test_functions_stay()
	{
		KbcModule module = StdPipeline.Compile(
			"""
			fun used() -> i32 7
			fun unused() -> i32 42

			#[test] fun smoke() {}

			fun main() -> i32 used()
			""").Module;

		DeadFunctionEliminator.Eliminate(module);

		Assert.True(module.TryFindFunction("used", 0, out _));
		Assert.True(module.TryFindFunction("main", 0, out _));
		Assert.True(module.TryFindFunction("smoke", 0, out _));
		Assert.False(module.TryFindFunction("unused", 0, out _));
		Assert.Equal(KValue.I32(7), Run(module, "main"));
	}

	[Fact]
	public void Inline_supports_branches_and_locals()
	{
		KbcModule module = StdPipeline.Compile(
			"""
			#[inline] fun clamp(i32 value) -> i32 {
				if value < 0 { ret 0 }
				if value > 10 { ret 10 }
				value
			}

			fun main() -> i32 clamp(5) + clamp(-1) + clamp(20)
			""").Module;
		Assert.True(module.TryFindFunction("clamp", 1, out int clampIndex));
		Assert.True(module.TryFindFunction("main", 0, out int mainIndex));
		Inliner.Inline(module);
		Assert.DoesNotContain(Calls(module.FunctionPool[mainIndex].Code), target => target == clampIndex);
		StackAnalyzer.Compute(module);
		Assert.Equal(KValue.I32(15), Run(module, "main"));
	}

	private static IEnumerable<int> Calls(int[] code)
	{
		var offset = 0;
		while (offset < code.Length)
		{
			var op = (OpCode)code[offset];
			int count = OpCodeInfo.OperandCount(op);
			if (op is OpCode.Call or OpCode.CallVirtual or OpCode.CallBase or OpCode.TailCall)
				yield return code[offset + 1];
			offset += 1 + count;
		}
	}

	private static KValue Run(KbcModule module, string function)
	{
		var interpreter = new Interpreter(module, new NativeResolver(StdPipeline.Catalog()));
		return interpreter.Run(function);
	}
}
