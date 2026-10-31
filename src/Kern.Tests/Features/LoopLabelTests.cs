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
/// Метки циклов (spec §3, раунд 27): <c>loop@outer</c>, <c>stop outer</c>, <c>next outer</c>
/// для всех форм цикла, лексическое разрешение, диагностики KRN2106 и очистка use при
/// выходе из помеченного цикла.
/// </summary>
public sealed class LoopLabelTests
{
	[Fact]
	public void Stop_label_exits_the_named_outer_loop()
	{
		Assert.Equal(KValue.I32(1), Run("""
			fun f() -> i32 {
				i32 count = 0
				loop@outer i32 i in 1..=3 {
					loop i32 j in 1..=3 {
						count += 1
						stop outer
					}
				}
				count
			}
			""", "f"));
	}

	[Fact]
	public void Next_label_advances_the_named_outer_loop()
	{
		Assert.Equal(KValue.I32(30), Run("""
			fun f() -> i32 {
				i32 sum = 0
				i32 guard = 0
				loop@outer i32 i in 1..=3 {
					loop i32 j in 1..=3 {
						guard += 1
						if guard > 100 { stop outer }
						if j == 2 { next outer }
						sum += 10
					}
					sum += 1
				}
				sum
			}
			""", "f"));
	}

	[Fact]
	public void Unlabeled_stop_and_next_still_target_the_nearest_loop()
	{
		Assert.Equal(KValue.I32(3), Run("""
			fun f() -> i32 {
				i32 hits = 0
				loop i32 i in 1..=3 {
					loop i32 j in 1..=3 {
						if j == 2 { stop }
						hits += 1
					}
				}
				hits
			}
			""", "f"));
		Assert.Equal(KValue.I32(6), Run("""
			fun f() -> i32 {
				i32 skips = 0
				loop i32 i in 1..=3 {
					loop i32 j in 1..=3 {
						if j == 2 { next }
						skips += 1
					}
				}
				skips
			}
			""", "f"));
	}

	[Fact]
	public void Labels_are_accepted_on_infinite_condition_iteration_and_do_while_loops()
	{
		Assert.Equal(KValue.I32(25), Run("""
			fun f() -> i32 {
				i32 n = 0
				loop@inf { n += 1; if n == 1 { stop inf } }
				loop@cond n < 2 { n += 1 }
				loop@iter i32 i in 1..=2 { n += i }
				loop@dw { n += 10 } : n < 20
				n
			}
			""", "f"));
	}

	[Fact]
	public void Async_loop_accepts_a_label_in_the_parser()
	{
		var parsed = Parser.Parse("""
			fun f() {
				async loop@stream i32 i in 1..=2 { }
			}
			""");
		Assert.Empty(parsed.Diagnostics);
	}

	[Fact]
	public void Stop_outer_disposes_use_frames_of_all_exited_blocks()
	{
		Assert.Equal(KValue.Str("inner,outer"), Run("""
			fun f() -> str {
				list<str> log = []
				loop@outer i32 i in 1..=1 {
					use var a = Res(log, "outer")
					loop i32 j in 1..=1 {
						use var b = Res(log, "inner")
						stop outer
					}
				}
				log.join(",")
			}

			unit Res(list<str> log, str name) {
				fun dispose() { log.add(name) }
			}
			""", "f"));
	}

	[Fact]
	public void Next_outer_disposes_use_frames_of_all_exited_blocks()
	{
		Assert.Equal(KValue.Str("inner,outer,inner,outer"), Run("""
			fun f() -> str {
				list<str> log = []
				loop@outer i32 i in 1..=2 {
					use var a = Res(log, "outer")
					loop i32 j in 1..=1 {
						use var b = Res(log, "inner")
						next outer
					}
				}
				log.join(",")
			}

			unit Res(list<str> log, str name) {
				fun dispose() { log.add(name) }
			}
			""", "f"));
	}

	[Fact]
	public void Unknown_label_reports_KRN2106()
	{
		var error = Assert.Single(
			CompileWithDiagnostics("""
				fun f() {
					loop { stop missing }
				}
				""").Diagnostics,
			diagnostic => diagnostic.Code == "KRN2106");
		Assert.True(error.IsError);
		Assert.Contains("Метка цикла 'missing' не найдена", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Duplicate_label_in_one_function_reports_KRN2106()
	{
		Assert.Contains(CompileWithDiagnostics("""
			fun f() {
				loop@same { loop@same { stop } }
			}
			""").Diagnostics, diagnostic =>
			diagnostic.Code == "KRN2106" && diagnostic.Message.Contains("уже объявлена", StringComparison.Ordinal));

		Assert.Contains(CompileWithDiagnostics("""
			fun f() {
				loop@same { stop }
				loop@same { stop }
			}
			""").Diagnostics, diagnostic =>
			diagnostic.Code == "KRN2106" && diagnostic.Message.Contains("уже объявлена", StringComparison.Ordinal));
	}

	[Fact]
	public void Bare_stop_does_not_consume_an_identifier_from_the_next_line()
	{
		Assert.Equal(KValue.I32(2), Run("""
			fun f() -> i32 {
				i32 n = 0
				loop {
					n += 1
					if n == 2 {
						stop
						n = 100
					}
				}
				n
			}
			""", "f"));
	}

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static KValue Run(string source, string function) =>
		new Interpreter(Compile(source).Module, new NativeResolver(Catalog())).Run(function, []);

	private static CodegenResult Compile(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);

		ModuleSymbol imported = NativeSymbolBuilder.Import(module, Catalog(), out var importDiagnostics);
		Assert.Empty(importDiagnostics);

		var bound = new Binder(imported, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);
		var generated = new CodeGenerator().Generate(bound);
		Assert.DoesNotContain(generated.Diagnostics, diagnostic => diagnostic.IsError);
		return generated;
	}

	private sealed record Outcome(CodegenResult? Module, IReadOnlyList<Diagnostic> Diagnostics);

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
