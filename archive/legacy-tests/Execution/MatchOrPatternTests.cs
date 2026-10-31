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
/// B4a2: or-паттерны <c>p1 | p2</c> в match — литералы, char, смесь диапазона и литерала,
/// учёт альтернатив в анализе покрытия и запрет связывания имён (KRN2114).
/// </summary>
public sealed class MatchOrPatternTests
{
	[Fact]
	public void Literal_or_pattern_matches_any_alternative()
	{
		const string source =
			"""
			fun two() -> i32 {
				match 2 { 1 | 2 => 10; _ => 20 }
			}

			fun three() -> i32 {
				match 3 { 1 | 2 => 10; _ => 20 }
			}
			""";

		AssertI32(10, RunSource(source, "two"), "2 должно совпасть с 1 | 2");
		AssertI32(20, RunSource(source, "three"), "3 не должно совпасть с 1 | 2");
	}

	[Fact]
	public void Char_or_pattern_matches_each_alternative()
	{
		const string source =
			"""
			fun b() -> i32 {
				match 'b' { 'a' | 'b' => 1; _ => 0 }
			}

			fun c() -> i32 {
				match 'c' { 'a' | 'b' => 1; _ => 0 }
			}
			""";

		AssertI32(1, RunSource(source, "b"), "'b' должно совпасть с 'a' | 'b'");
		AssertI32(0, RunSource(source, "c"), "'c' не должно совпасть с 'a' | 'b'");
	}

	[Fact]
	public void Or_pattern_mixes_range_and_literal()
	{
		const string source =
			"""
			fun two() -> i32 {
				match 2 { 1..=3 | 5 => 10; _ => 20 }
			}

			fun five() -> i32 {
				match 5 { 1..=3 | 5 => 10; _ => 20 }
			}

			fun four() -> i32 {
				match 4 { 1..=3 | 5 => 10; _ => 20 }
			}
			""";

		AssertI32(10, RunSource(source, "two"), "2 входит в 1..=3");
		AssertI32(10, RunSource(source, "five"), "5 покрыт литеральной альтернативой");
		AssertI32(20, RunSource(source, "four"), "4 не входит ни в одну альтернативу");
	}

	[Fact]
	public void Or_pattern_alternatives_count_for_coverage()
	{
		var exhaustive = Diagnostics("""
			fun f(bool flag) {
				match flag { true | false => 1 }
			}
			""");
		Assert.DoesNotContain(exhaustive, diagnostic => diagnostic.Code == "KRN2110");

		var unreachable = Diagnostics("""
			fun f(i32 x) {
				match x { _ | 1 => 1; 2 => 2 }
			}
			""");
		Assert.Contains(unreachable, diagnostic => diagnostic.Code == "KRN2111" && !diagnostic.IsError);
	}

	[Fact]
	public void Binding_alternative_in_or_pattern_reports_KRN2114()
	{
		var diagnostics = Diagnostics("""
			fun f(i32 x) {
				match x { 1 | x => 1; _ => 0 }
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2114" && diagnostic.IsError);
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
