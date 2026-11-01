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
/// <c>kern.time</c> (spec §18c): <c>duration</c>, <c>datetime</c>, <c>stopwatch</c>,
/// <c>time.sleep/measure/unix_ms</c>.
/// </summary>
public sealed class TimeModuleTests
{
	[Fact]
	public void Duration_parses_and_reports_totals()
	{
		const string source = """
			fun minutes() -> i64 duration.parse("1h30m").total_minutes
			fun text() -> str duration.parse("1h30m").to_str()
			""";

		Assert.Equal(KValue.I64(90), Run(source, "minutes"));
		Assert.Equal(KValue.Str("1h 30m"), Run(source, "text"));
	}

	[Fact]
	public void Duration_arithmetic_and_comparison()
	{
		const string source = """
			fun doubled() -> i64 (duration.from_seconds(90) * 2).total_minutes
			fun plus() -> i64 (duration.from_seconds(90) + duration.from_ms(500)).total_ms
			fun greater() -> bool duration.from_seconds(90) > duration.from_minutes(1)
			""";

		Assert.Equal(KValue.I64(3), Run(source, "doubled"));
		Assert.Equal(KValue.I64(90_500), Run(source, "plus"));
		Assert.Equal(KValue.Bool(true), Run(source, "greater"));
	}

	[Fact]
	public void Invalid_duration_reports_a_russian_panic()
	{
		const string source = """
			fun main {
				println(duration.parse("nope"))
			}
			""";

		var module = Compile(source);
		var interpreter = new Interpreter(module.Module, new NativeResolver(Catalog()));
		var exception = Assert.Throws<KernPanic>(() => interpreter.Run("main"));
		Assert.Contains("duration.parse", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Datetime_fields_format_and_weekday()
	{
		const string source = """
			fun year() -> i64 datetime.of(2026, 10, 14, 12, 30, 0).year
			fun weekday() -> i64 datetime.of(2026, 10, 14, 0, 0, 0).weekday
			fun text() -> str datetime.of(2026, 10, 14, 12, 30, 0).format("yyyy-MM-dd HH:mm")
			""";

		Assert.Equal(KValue.I64(2026), Run(source, "year"));
		Assert.Equal(KValue.I64(3), Run(source, "weekday"));
		Assert.Equal(KValue.Str("2026-10-14 12:30"), Run(source, "text"));
	}

	[Fact]
	public void Datetime_arithmetic_and_add_months()
	{
		const string source = """
			fun later() -> str {
				datetime launch = datetime.of(2026, 10, 14, 12, 30, 0)
				(launch + duration.from_hours(36)).format("yyyy-MM-dd HH:mm")
			}

			fun diff() -> i64 {
				datetime launch = datetime.of(2026, 10, 14, 12, 30, 0)
				((launch + duration.from_hours(36)) - launch).total_hours
			}

			fun months() -> str
				datetime.of(2026, 10, 14, 0, 0, 0).add_months(3).format("dd.MM.yyyy")
			""";

		Assert.Equal(KValue.Str("2026-10-16 00:30"), Run(source, "later"));
		Assert.Equal(KValue.I64(36), Run(source, "diff"));
		Assert.Equal(KValue.Str("14.01.2027"), Run(source, "months"));
	}

	[Fact]
	public void Stopwatch_elapsed_grows_after_sleep()
	{
		const string source = """
			fun elapsed() -> bool {
				stopwatch sw()
				time.sleep(20)
				sw.elapsed.total_ms >= 10
			}
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "elapsed"));
	}

	[Fact]
	public void Time_measure_returns_a_duration()
	{
		const string source = """
			fun measured() -> bool {
				var took = time.measure(fun { time.sleep(5) })
				took.total_ms >= 1
			}
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "measured"));
	}

	[Fact]
	public void Unix_time_is_positive()
	{
		const string source = """
			fun seconds() -> bool time.unix_s() > 0
			fun millis() -> bool time.unix_ms() > 0
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "seconds"));
		Assert.Equal(KValue.Bool(true), Run(source, "millis"));
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
