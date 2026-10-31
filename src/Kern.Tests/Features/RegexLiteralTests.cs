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
/// Regex-литералы (spec §3/§12): `x"…"` — шаблон без escape-раскодирования,
/// `fx"…"` — интерполяция + regex; оба сворачиваются в вызов `kern.regex.regex(pattern)`.
/// </summary>
public sealed class RegexLiteralTests
{
	[Fact]
	public void Metadata_literal_becomes_a_regex_value()
	{
		const string source = """
			fun matches() -> bool {
				regex digits = x"\d+"
				digits.is_match("abc 123")
			}

			fun misses() -> bool x"\d+".is_match("abc")
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "matches"));
		Assert.Equal(KValue.Bool(false), Run(source, "misses"));
	}

	[Fact]
	public void Metadata_literal_find_returns_the_match_or_nil()
	{
		const string source = """
			fun found() -> str x"\d+".find("abc 123") ?: "none"
			fun missing() -> str x"\d+".find("abc") ?: "none"
			""";

		Assert.Equal(KValue.Str("123"), Run(source, "found"));
		Assert.Equal(KValue.Str("none"), Run(source, "missing"));
	}

	[Fact]
	public void Interpolated_metadata_literal_uses_the_expression()
	{
		const string source = """
			fun five() -> bool {
				i32 n = 5
				regex anchored = fx"^${n}$"
				anchored.is_match("5")
			}

			fun six() -> bool {
				i32 n = 5
				fx"^${n}$".is_match("6")
			}
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "five"));
		Assert.Equal(KValue.Bool(false), Run(source, "six"));
	}

	[Fact]
	public void Invalid_regex_literal_panics_with_a_russian_message()
	{
		const string source = """
			fun main {
				regex broken = x"["
				println(broken)
			}
			""";

		var module = Compile(source);
		var interpreter = new Interpreter(module.Module, new NativeResolver(Catalog()));
		var exception = Assert.Throws<KernPanic>(() => interpreter.Run("main"));
		Assert.Contains("regex", exception.Message, StringComparison.Ordinal);
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
