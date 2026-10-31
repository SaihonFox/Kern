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
/// Экспоненты в числовых литералах (spec §3): <c>1e3</c>, <c>1e-6</c>, <c>1.5e+3</c>,
/// <c>2E10</c>, разделители <c>_</c> и суффиксы. Некорректные формы — <c>LEX005</c>.
/// </summary>
public sealed class NumericLiteralTests
{
	[Fact]
	public void Exponent_literals_parse_to_f64()
	{
		const string source = """
			fun a() -> f64 1e3
			fun b() -> f64 2E10
			fun c() -> f64 1.5e+3
			fun d() -> f64 1_000e-3
			fun e() -> f64 1e-6
			""";

		Assert.Equal(KValue.F64(1000.0), Run(source, "a"));
		Assert.Equal(KValue.F64(20_000_000_000.0), Run(source, "b"));
		Assert.Equal(KValue.F64(1500.0), Run(source, "c"));
		Assert.Equal(KValue.F64(1.0), Run(source, "d"));
		Assert.Equal(KValue.F64(0.000001), Run(source, "e"));
	}

	[Fact]
	public void Exponent_literals_keep_suffixes_and_round_trip()
	{
		const string source = """
			fun f32_value() -> bool 1.5e2f32 == 150.0f32
			fun round_trip() -> bool 1e-6 * 1e6 == 1.0
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "f32_value"));
		Assert.Equal(KValue.Bool(true), Run(source, "round_trip"));
	}

	[Theory]
	[InlineData("1e")]
	[InlineData("1e+")]
	[InlineData("1.5e-x")]
	[InlineData("1e_")]
	public void Malformed_exponents_report_LEX005(string literal)
	{
		var source = $"fun main {{ f64 x = {literal} }}";
		var outcome = CompileWithDiagnostics(source);
		Assert.Contains(outcome.Diagnostics, diagnostic => diagnostic.Code == "LEX005");
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
