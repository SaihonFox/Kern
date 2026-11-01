using Kern.Bytecode;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Optimization;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// M11: peephole <c>-O2</c> — <c>Not; JmpIf</c> → <c>JmpIfNot</c>, удаление перехода на
/// следующую инструкцию, пар «загрузка + Pop», недостижимого кода; результаты не меняются.
/// </summary>
public sealed class PeepholeOptimizerTests
{
	[Fact]
	public void Not_before_conditional_jump_becomes_inverted_jump()
	{
		var module = new KbcModule { Name = "peep" };
		module.AddFunction(new FunctionDef
		{
			Name = "f",
			ParamTypes = [TypeSig.Bool],
			ReturnType = TypeSig.I32,
			LocalCount = 1,
			MaxStack = 1,
			Code =
			[
				(int)OpCode.LoadLocal, 0,
				(int)OpCode.Not,
				(int)OpCode.JmpIf, 8,
				(int)OpCode.PushI32, 1,
				(int)OpCode.Ret,
				(int)OpCode.PushI32, 0,
				(int)OpCode.Ret,
			],
		});

		KbcModule before = RoundTrip(module);
		KbcModule after = RoundTrip(module);
		PeepholeOptimizer.Optimize(after);

		Assert.Equal(KValue.I32(1), Run(before, "f", KValue.Bool(true)));
		Assert.Equal(KValue.I32(0), Run(before, "f", KValue.Bool(false)));
		Assert.Equal(KValue.I32(1), Run(after, "f", KValue.Bool(true)));
		Assert.Equal(KValue.I32(0), Run(after, "f", KValue.Bool(false)));

		Assert.DoesNotContain((int)OpCode.Not, after.FunctionPool[0].Code);
		Assert.DoesNotContain((int)OpCode.JmpIf, after.FunctionPool[0].Code);
		Assert.Contains((int)OpCode.JmpIfNot, after.FunctionPool[0].Code);
		Assert.True(after.FunctionPool[0].Code.Length < before.FunctionPool[0].Code.Length);
	}

	[Fact]
	public void Unreachable_code_after_return_is_removed()
	{
		var module = new KbcModule { Name = "dead" };
		module.AddFunction(new FunctionDef
		{
			Name = "f",
			ReturnType = TypeSig.I32,
			MaxStack = 1,
			Code =
			[
				(int)OpCode.PushI32, 1,
				(int)OpCode.Ret,
				(int)OpCode.PushI32, 2,
				(int)OpCode.Ret,
			],
		});

		PeepholeOptimizer.Optimize(module);

		Assert.Equal(KValue.I32(1), Run(module, "f"));
		Assert.DoesNotContain(2, module.FunctionPool[0].Code);
	}

	[Fact]
	public void Compiled_program_keeps_results_after_o2()
	{
		const string source = """
			fun fib(i32 n) -> i32 {
				if n < 2 { n } else { fib(n - 1) + fib(n - 2) }
			}
			fun branch(bool flag) -> i32 {
				if !flag { 10 } else { 20 }
			}
			fun loops(i32 n) -> i32 {
				i32 total = 0
				loop i32 i in 1..=n total += i
				total
			}
			""";

		CodegenResult generated = Compile(source);
		KbcModule before = RoundTrip(generated.Module);
		KbcModule after = RoundTrip(generated.Module);
		ConstantFolder.Fold(after);
		PeepholeOptimizer.Optimize(after);

		Assert.Equal(KValue.I32(55), Run(before, "fib", KValue.I32(10)));
		Assert.Equal(KValue.I32(55), Run(after, "fib", KValue.I32(10)));
		// `if !flag` — ветвь then выполняется при flag == false.
		Assert.Equal(KValue.I32(20), Run(before, "branch", KValue.Bool(true)));
		Assert.Equal(KValue.I32(20), Run(after, "branch", KValue.Bool(true)));
		Assert.Equal(KValue.I32(10), Run(after, "branch", KValue.Bool(false)));
		Assert.Equal(KValue.I32(15), Run(after, "loops", KValue.I32(5)));
	}

	private static KbcModule RoundTrip(KbcModule module) => KbcReader.Read(KbcWriter.Write(module));

	private static KValue Run(KbcModule module, string function, params KValue[] arguments) =>
		new Interpreter(module).Run(function, arguments);

	private static CodegenResult Compile(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);

		var bound = new Binder(module, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);
		var generated = new CodeGenerator().Generate(bound);
		Assert.DoesNotContain(generated.Diagnostics, diagnostic => diagnostic.IsError);
		return generated;
	}
}
