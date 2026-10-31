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

namespace Kern.Tests.Features;

/// <summary>
/// Форматирование дробных (Q31, spec §3): в окне <c>[1e-6, 1e15)</c> — десятичная запись
/// без экспоненты (кратчайшая с round-trip), вне окна — научная; <c>f32</c> печатается
/// кратчайшим представлением своего типа (без артефактов double).
/// </summary>
public sealed class FloatFormattingTests
{
	[Fact]
	public void Values_inside_the_window_print_without_an_exponent()
	{
		const string source = """
			fun a() -> str { f64 v = 1e-6  v.to_str() }
			fun b() -> str { f64 v = 0.0001  v.to_str() }
			fun c() -> str { f64 v = 2.5  v.to_str() }
			fun d() -> str { f64 v = 1e6  v.to_str() }
			""";

		Assert.Equal(KValue.Str("0.000001"), Run(source, "a"));
		Assert.Equal(KValue.Str("0.0001"), Run(source, "b"));
		Assert.Equal(KValue.Str("2.5"), Run(source, "c"));
		Assert.Equal(KValue.Str("1000000"), Run(source, "d"));
	}

	[Fact]
	public void Values_outside_the_window_use_scientific_notation()
	{
		const string source = """
			fun tiny() -> str { f64 v = 1e-20  v.to_str() }
			fun huge() -> str { f64 v = 1e20  v.to_str() }
			""";

		Assert.Equal(KValue.Str("1E-20"), Run(source, "tiny"));
		Assert.Equal(KValue.Str("1E+20"), Run(source, "huge"));
	}

	[Fact]
	public void F32_prints_its_shortest_representation()
	{
		const string source = """
			fun scaled() -> str { f32 v = 1.5e2f32  v.to_str() }
			fun tenth() -> str { f32 v = 0.1f32  v.to_str() }
			""";

		Assert.Equal(KValue.Str("150"), Run(source, "scaled"));
		Assert.Equal(KValue.Str("0.1"), Run(source, "tenth"));
	}

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static KValue Run(string source, string function, params KValue[] arguments) =>
		new Interpreter(Compile(source).Module, new NativeResolver(Catalog())).Run(function, arguments);

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
