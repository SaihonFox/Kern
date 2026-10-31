using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

/// <summary>
/// B4a: диапазоны в match — включительный `1..=5`, исключительный `1..5`, char-диапазоны
/// и диагностики границ (не литерал, чужой тип, пустой/перевёрнутый диапазон).
/// </summary>
public sealed class MatchRangeTests
{
	[Fact]
	public void Inclusive_range_matches_bounds_and_falls_through_outside()
	{
		const string source =
			"""
			fun inside() -> i32 {
				match 3 { 1..=5 => 10; _ => 20 }
			}

			fun outside() -> i32 {
				match 6 { 1..=5 => 10; _ => 20 }
			}
			""";

		AssertI32(10, RunSource(source, "inside"), "3 должно попасть в 1..=5");
		AssertI32(20, RunSource(source, "outside"), "6 не должно попасть в 1..=5");
	}

	[Fact]
	public void Exclusive_range_excludes_the_end()
	{
		const string source =
			"""
			fun first() -> i32 {
				match 1 { 1..5 => 10; _ => 20 }
			}

			fun lastInside() -> i32 {
				match 4 { 1..5 => 10; _ => 20 }
			}

			fun endExcluded() -> i32 {
				match 5 { 1..5 => 10; _ => 20 }
			}
			""";

		AssertI32(10, RunSource(source, "first"), "1 входит в 1..5");
		AssertI32(10, RunSource(source, "lastInside"), "4 входит в 1..5");
		AssertI32(20, RunSource(source, "endExcluded"), "5 не входит в 1..5");
	}

	[Fact]
	public void Char_range_matches_lowercase_only()
	{
		const string source =
			"""
			fun lower() -> i32 {
				match 'm' { 'a'..='z' => 1; _ => 0 }
			}

			fun upper() -> i32 {
				match 'M' { 'a'..='z' => 1; _ => 0 }
			}
			""";

		AssertI32(1, RunSource(source, "lower"), "'m' должно попасть в 'a'..='z'");
		AssertI32(0, RunSource(source, "upper"), "'M' не должно попасть в 'a'..='z'");
	}

	[Fact]
	public void Range_bound_diagnostics_use_stable_codes()
	{
		var nonLiteral = Diagnostics("""
			fun f(i32 x, i32 n) {
				match x { 1..=n => 1; _ => 0 }
			}
			""");
		Assert.Contains(nonLiteral, diagnostic => diagnostic.Code == "KRN2114" && diagnostic.IsError);

		var wrongType = Diagnostics("""
			fun f(i32 x) {
				match x { 1..='z' => 1; _ => 0 }
			}
			""");
		Assert.Contains(wrongType, diagnostic => diagnostic.Code == "KRN2112" && diagnostic.IsError);

		var reversed = Diagnostics("""
			fun f(i32 x) {
				match x { 5..=1 => 1; _ => 0 }
			}
			""");
		Assert.Contains(reversed, diagnostic => diagnostic.Code == "KRN2113" && diagnostic.IsError);

		var empty = Diagnostics("""
			fun f(i32 x) {
				match x { 5..5 => 1; _ => 0 }
			}
			""");
		Assert.Contains(empty, diagnostic => diagnostic.Code == "KRN2113" && diagnostic.IsError);
	}

	private static void AssertI32(int expected, KValue actual, string message) =>
		Assert.True(
			actual.Equals(KValue.I32(expected)),
			$"{message}: ожидалось {expected}, получено {actual}");

	/// <summary>Парсер → объявления → Binder → CodeGenerator → Interpreter; ошибки компиляции — падение.</summary>
	private static KValue RunSource(string source, string function)
	{
		var parsed = Parser.Parse(source);
		Assert.True(parsed.Diagnostics.Count == 0, "Парсер вернул ошибки: " + Describe(parsed.Diagnostics));
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.True(tableDiagnostics.Count == 0, "Сбор символов вернул ошибки: " + Describe(tableDiagnostics));
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.True(collectorDiagnostics.Count == 0, "Сбор объявлений вернул ошибки: " + Describe(collectorDiagnostics));
		var bound = new Binder(module, table).Bind();
		Assert.True(
			bound.Diagnostics.All(diagnostic => !diagnostic.IsError),
			"Binder вернул ошибки: " + Describe(bound.Diagnostics));
		var result = new CodeGenerator().Generate(bound);
		Assert.True(
			result.Diagnostics.All(diagnostic => !diagnostic.IsError),
			"CodeGenerator вернул ошибки: " + Describe(result.Diagnostics));
		return new Interpreter(result.Module).Run(function, []);
	}

	private static IReadOnlyList<Diagnostic> Diagnostics(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		var bound = new Binder(module, table).Bind();
		return bound.Diagnostics;
	}

	private static string Describe(IEnumerable<Diagnostic> diagnostics) => string.Join("; ", diagnostics);
}
