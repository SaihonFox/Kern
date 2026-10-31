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
/// Итерация по протоколу <c>iter&lt;T&gt;</c> (spec §16): тип объявляет <c>iter&lt;T&gt;</c>
/// родителем и реализует <c>~fun next -> T?</c>; цикл вызывает <c>next()</c> до <c>nil</c>.
/// Проверяются сумма, <c>stop</c>, привязки <c>var</c>/тип, loop-выражение и диагностики.
/// </summary>
public sealed class IterProtocolTests
{
	private const string Counter = """
		type Counter(i32 limit)[iter<i32>] {
			i32 current = 0

			~fun next() -> i32? {
				if $current >= $limit { ret nil }
				$current += 1
				$current
			}
		}
		""";

	[Fact]
	public void Counter_type_sums_through_iter_protocol()
	{
		Assert.Equal(KValue.I32(6), Run($$"""
			{{Counter}}

			fun f() -> i32 {
				i32 sum = 0
				loop i32 x in Counter(3) { sum += x }
				sum
			}
			""", "f"));
	}

	[Fact]
	public void Stop_and_labels_work_inside_protocol_iteration()
	{
		Assert.Equal(KValue.I32(1), Run($$"""
			type Endless()[iter<i32>] {
				i32 current = 0

				~fun next() -> i32? {
					$current += 1
					$current
				}
			}

			fun f() -> i32 {
				i32 hits = 0
				loop@outer i32 x in Endless() {
					loop i32 y in Endless() {
						hits += 1
						stop outer
					}
				}
				hits
			}
			""", "f"));
	}

	[Fact]
	public void Var_binding_and_typed_binding_convert_elements()
	{
		Assert.Equal(KValue.I32(6), Run($$"""
			{{Counter}}

			fun inferred() -> i32 {
				i32 sum = 0
				loop var x in Counter(3) { sum += x }
				sum
			}

			fun typed() -> i32 {
				i32 sum = 0
				loop i32 x in Counter(3) { sum += x }
				sum
			}
			""", "inferred"));

		Assert.Equal(KValue.I32(6), Run($$"""
			{{Counter}}

			fun typed() -> i32 {
				i32 sum = 0
				loop i32 x in Counter(3) { sum += x }
				sum
			}
			""", "typed"));
	}

	[Fact]
	public void Loop_expression_collects_protocol_elements()
	{
		Assert.Equal(KValue.I32(12), Run($$"""
			{{Counter}}

			fun f() -> i32 {
				list<i32> doubled = loop i32 x in Counter(3) x * 2
				doubled[0] + doubled[1] + doubled[2]
			}
			""", "f"));
	}

	[Fact]
	public void Missing_next_reports_KRN2101()
	{
		var diagnostic = Assert.Single(
			CompileWithDiagnostics("""
				type Broken()[iter<i32>] {
					fun value() -> i32 { 1 }
				}

				fun f() {
					loop i32 x in Broken() { }
				}
				""").Diagnostics,
			item => item.Code == "KRN2101");
		Assert.True(diagnostic.IsError);
		Assert.Contains("next", diagnostic.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Wrong_next_return_type_reports_KRN2101()
	{
		var diagnostic = Assert.Single(
			CompileWithDiagnostics("""
				type Wrong()[iter<i32>] {
					~fun next() -> i32 {
						1
					}
				}

				fun f() {
					loop i32 x in Wrong() { }
				}
				""").Diagnostics,
			item => item.Code == "KRN2101");
		Assert.True(diagnostic.IsError);
		Assert.Contains("i32?", diagnostic.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Async_iter_is_reported_as_an_explicit_gap()
	{
		var diagnostic = Assert.Single(
			CompileWithDiagnostics("""
				type Stream(i32 limit)[async_iter<i32>] {
					~async fun next -> task<i32?> {
						nil
					}
				}

				async fun f() {
					loop i32 x in Stream(1) { }
				}
				""").Diagnostics,
			item => item.Code == "KRN2101");
		Assert.True(diagnostic.IsError);
		Assert.Contains("async_iter", diagnostic.Message, StringComparison.Ordinal);
	}

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static KValue Run(string source, string function) =>
		new Interpreter(Compile(source).Module, new NativeResolver(Catalog())).Run(function, []);

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

		ModuleSymbol imported = NativeSymbolBuilder.Import(module, Catalog(), out var importProblems);
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
