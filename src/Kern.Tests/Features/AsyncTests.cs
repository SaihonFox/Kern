using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Async/await (spec §M5): <c>async fun</c> возвращает <c>task&lt;T&gt;</c> (VM оборачивает
/// результат в <c>KTask</c>), <c>await</c> допустим только внутри async-функции и только
/// для <c>task&lt;T&gt;</c>; <c>await delay(1)</c> припарковывает состояние и возобновляется
/// планировщиком; паника внутри async всплывает при <c>await</c>.
/// </summary>
public sealed class AsyncTests
{
	[Fact]
	public void Async_function_returns_a_task_and_await_yields_the_value()
	{
		const string source = """
			async fun add(i32 a, i32 b) -> i32 {
				await delay(1)
				a + b
			}

			async fun main -> i32 {
				await add(2, 3)
			}
			""";

		Assert.Equal(KValue.I32(5), RunAwait(source, "main"));
	}

	[Fact]
	public void Sequential_awaits_run_in_order()
	{
		const string source = """
			async fun add(i32 a, i32 b) -> i32 {
				await delay(1)
				a + b
			}

			async fun main -> i32 {
				(await add(2, 3)) + (await add(4, 5))
			}
			""";

		Assert.Equal(KValue.I32(14), RunAwait(source, "main"));
	}

	[Fact]
	public void Task_value_can_be_awaited_later()
	{
		const string source = """
			async fun square(i32 x) -> i32 {
				await delay(1)
				x * x
			}

			async fun main -> i32 {
				task<i32> pending = square(5)
				(await pending) + (await square(3))
			}
			""";

		Assert.Equal(KValue.I32(34), RunAwait(source, "main"));
	}

	[Fact]
	public void Await_outside_async_reports_KRN2101()
	{
		const string source = """
			fun no_async() -> i32 {
				await delay(1)
				1
			}

			fun main {
				println(no_async())
			}
			""";

		var outcome = CompileWithDiagnostics(source);
		Assert.Contains(
			outcome.Diagnostics,
			diagnostic => diagnostic.Code == "KRN2101" &&
				diagnostic.Message.Contains("только внутри async", StringComparison.Ordinal));
	}

	[Fact]
	public void Await_on_a_non_task_reports_KRN2101()
	{
		const string source = """
			async fun bad() -> i32 { await 1 }
			fun main { println(bad()) }
			""";

		var outcome = CompileWithDiagnostics(source);
		Assert.Contains(
			outcome.Diagnostics,
			diagnostic => diagnostic.Code == "KRN2101" &&
				diagnostic.Message.Contains("ожидает 'task<T>'", StringComparison.Ordinal));
	}

	[Fact]
	public void Discarded_async_call_reports_KRN2305()
	{
		const string source = """
			async fun add(i32 a, i32 b) -> i32 { a + b }
			fun main { add(1, 2) }
			""";

		var outcome = CompileWithDiagnostics(source);
		Assert.Contains(
			outcome.Diagnostics,
			diagnostic => diagnostic.Code == "KRN2305" && !diagnostic.IsError);
	}

	[Fact]
	public void Panic_inside_async_surfaces_when_awaited()
	{
		const string source = """
			async fun boom() -> i32 {
				await delay(1)
				i32 zero = 0
				10 / zero
			}

			async fun main -> i32 {
				await boom()
			}
			""";

		var module = Compile(source);
		var interpreter = new Interpreter(module.Module, new NativeResolver(Catalog()));
		var exception = Assert.Throws<KernPanic>(() => interpreter.Run("main"));
		Assert.Contains("деление на ноль", exception.Message, StringComparison.Ordinal);
	}

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	/// <summary>Запуск async-точки входа: результат приходит готовым <c>KTask</c>.</summary>
	private static KValue RunAwait(string source, string function, params KValue[] arguments)
	{
		var module = Compile(source);
		var interpreter = new Interpreter(module.Module, new NativeResolver(Catalog()));
		KValue value = interpreter.Run(function, arguments);
		if (value.Ref is not KTask task)
			return value;
		Assert.True(task.IsCompleted, "задача должна завершиться до возврата Run");
		if (task.Status == KTaskStatus.Failed)
			throw task.Error ?? new KernPanic("задача завершилась ошибкой");
		return task.Result;
	}

	private static CodegenResult Compile(string source)
	{
		var outcome = CompileWithDiagnostics(source);
		Assert.DoesNotContain(outcome.Diagnostics, diagnostic => diagnostic.IsError);
		Assert.NotNull(outcome.Module);
		return outcome.Module!;
	}

	private sealed record Outcome(CodegenResult? Module, IReadOnlyList<Diagnostic> Diagnostics);

	/// <summary>Полный конвейер одного файла с сохранением диагностик.</summary>
	private static Outcome CompileWithDiagnostics(string source)
	{
		var diagnostics = new List<Diagnostic>();
		var parsed = Parser.Parse(source);
		diagnostics.AddRange(parsed.Diagnostics);
		if (HasErrors(diagnostics))
			return new Outcome(null, diagnostics);

		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		diagnostics.AddRange(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		diagnostics.AddRange(collectorDiagnostics);

		var catalog = Catalog();
		ModuleSymbol imported = NativeSymbolBuilder.Import(module, catalog, out var importProblems);
		Assert.Empty(importProblems);

		var bound = new Binder(imported, table).Bind();
		diagnostics.AddRange(bound.Diagnostics);
		if (HasErrors(diagnostics))
			return new Outcome(null, diagnostics);

		var generated = new CodeGenerator().Generate(bound);
		diagnostics.AddRange(generated.Diagnostics);
		return HasErrors(diagnostics)
			? new Outcome(null, diagnostics)
			: new Outcome(generated, diagnostics);
	}

	private static bool HasErrors(IReadOnlyList<Diagnostic> diagnostics) =>
		diagnostics.Any(diagnostic => diagnostic.IsError);
}
