using Assembly = System.Reflection.Assembly;
using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features.Support;

/// <summary>Полный конвейер одного файла для тестов std-модулей (M7).</summary>
internal static class StdPipeline
{
	public static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	public static NativeCatalog Catalog(params Assembly[] extra) =>
		extra.Length == 0
			? Catalog()
			: NativeCatalog.Scan([typeof(Kern.Runtime.Core.CoreModule).Assembly, .. extra]);

	public static KValue Run(string source, string function, params KValue[] arguments) =>
		RunWithLibraries(source, function, [], arguments);

	public static KValue RunWithLibraries(
		string source,
		string function,
		Assembly[] libraries,
		params KValue[] arguments)
	{
		NativeCatalog catalog = Catalog(libraries);
		return new Interpreter(Compile(source, catalog).Module, new NativeResolver(catalog))
			.Run(function, arguments);
	}

	/// <summary>Запуск async-функции: возвращает результат завершённой задачи.</summary>
	public static KValue RunAsync(string source, string function, params KValue[] arguments)
	{
		KValue value = Run(source, function, arguments);
		if (value.Ref is not Kern.Vm.Async.KTask task)
			return value;
		Assert.True(task.IsCompleted, "задача должна завершиться до возврата Run");
		return task.Unwrap();
	}

	public static KernPanic RunPanics(string source, string function)
	{
		var interpreter = new Interpreter(Compile(source).Module, new NativeResolver(Catalog()));
		return Assert.Throws<KernPanic>(() => interpreter.Run(function));
	}

	public static CodegenResult Compile(string source) => Compile(source, Catalog());

	public static CodegenResult Compile(string source, NativeCatalog catalog)
	{
		var outcome = CompileWithDiagnostics(source, catalog);
		Assert.DoesNotContain(outcome.Diagnostics, diagnostic => diagnostic.IsError);
		Assert.NotNull(outcome.Module);
		return outcome.Module!;
	}

	public static CodegenResult? CompileOrNull(string source, out IReadOnlyList<Diagnostic> diagnostics)
	{
		var outcome = CompileWithDiagnostics(source, Catalog());
		diagnostics = outcome.Diagnostics;
		return outcome.Module;
	}

	private sealed record Outcome(CodegenResult? Module, IReadOnlyList<Diagnostic> Diagnostics);

	private static Outcome CompileWithDiagnostics(string source, NativeCatalog catalog)
	{
		var diagnostics = new List<Diagnostic>();
		var parsed = Parser.Parse(source);
		diagnostics.AddRange(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		diagnostics.AddRange(tableDiagnostics);

		var nativeTypes = NativeSymbolBuilder.CreateTypeSymbols(catalog);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(
			parsed.Tree,
			table,
			"main",
			primes: null,
			external: null,
			nativeTypes);
		diagnostics.AddRange(collectorDiagnostics);
		if (HasErrors(diagnostics))
			return new Outcome(null, diagnostics);

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
