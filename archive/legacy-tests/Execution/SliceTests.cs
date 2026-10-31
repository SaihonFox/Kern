using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

/// <summary>
/// B5b2: индексация строк и срезы (spec §3, round 27). <c>s[i]</c> даёт <c>char</c>;
/// <c>xs[a..b]</c>/<c>xs[a..=b]</c> — новый <c>list&lt;T&gt;</c> (у array тоже список),
/// <c>s[a..b]</c>/<c>s[a..=b]</c> — новая <c>str</c>. Отрицательные границы считаются
/// с конца, некорректный диапазон — паника, шаг и срез словаря — KRN2101,
/// несводимая к <c>i32</c> граница — KRN2102.
/// </summary>
public sealed class SliceTests
{
	[Fact]
	public void Str_index_returns_char_and_panics_out_of_range()
	{
		const string source = """
			fun first() -> char { "abc"[0] }
			fun last() -> char { "abc"[-1] }
			fun past_the_end() -> char { "abc"[3] }
			""";

		Assert.Equal(KValue.Char('a'), Run(source, "first"));
		Assert.Equal(KValue.Char('c'), Run(source, "last"));
		Assert.Throws<KernPanic>(() => Run(source, "past_the_end"));
	}

	[Fact]
	public void List_slice_copies_exclusive_and_inclusive_ranges()
	{
		const string source = """
			fun exclusive() -> list<i32> {
				list<i32> xs = [1, 2, 3, 4]
				xs[1..3]
			}

			fun inclusive() -> list<i32> {
				list<i32> xs = [1, 2, 3, 4]
				xs[1..=3]
			}
			""";

		var exclusive = Assert.IsType<KList>(Run(source, "exclusive").Ref);
		Assert.Equal(2, exclusive.Count);
		Assert.Equal(KValue.I32(2), exclusive[0]);
		Assert.Equal(KValue.I32(3), exclusive[1]);

		var inclusive = Assert.IsType<KList>(Run(source, "inclusive").Ref);
		Assert.Equal(3, inclusive.Count);
		Assert.Equal(KValue.I32(2), inclusive[0]);
		Assert.Equal(KValue.I32(3), inclusive[1]);
		Assert.Equal(KValue.I32(4), inclusive[2]);
	}

	[Fact]
	public void Array_slice_returns_a_list_copy()
	{
		// Срез array даёт именно list<i32> (копию), а не array: IsType<KArray> здесь упал бы.
		const string source = """
			fun values() -> list<i32> {
				array<i32> xs = <i32>[1, 2, 3, 4]
				xs[1..3]
			}
			""";

		var values = Assert.IsType<KList>(Run(source, "values").Ref);
		Assert.Equal(2, values.Count);
		Assert.Equal(KValue.I32(2), values[0]);
		Assert.Equal(KValue.I32(3), values[1]);
	}

	[Fact]
	public void Str_slice_returns_a_new_string()
	{
		// Отрицательные границы считаются с конца: "abc"[-2..-1] — это "b".
		const string source = """
			fun exclusive() -> str { "abc"[1..3] }
			fun inclusive() -> str { "abc"[1..=2] }
			fun negative() -> str { "abc"[-2..-1] }
			""";

		Assert.Equal(KValue.Str("bc"), Run(source, "exclusive"));
		Assert.Equal(KValue.Str("bc"), Run(source, "inclusive"));
		Assert.Equal(KValue.Str("b"), Run(source, "negative"));
	}

	[Fact]
	public void Invalid_slices_panic_or_report_binder_errors()
	{
		const string panics = """
			fun list_past_the_end() -> list<i32> {
				list<i32> xs = [1, 2]
				xs[0..5]
			}

			fun str_past_the_end() -> str { "abc"[0..5] }
			""";

		Assert.Throws<KernPanic>(() => Run(panics, "list_past_the_end"));
		Assert.Throws<KernPanic>(() => Run(panics, "str_past_the_end"));

		// Граница не приводится к i32 — KRN2102 из Convert.
		Assert.Equal(
			"KRN2102",
			Assert.Single(
				Diagnostics(
					"""
					fun main {
						list<i32> xs = [1, 2, 3]
						xs["a"..2]
					}
					"""),
				item => item.Code == "KRN2102").Code);

		// Срез словаря не поддержан — KRN2101.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics(
					"""
					fun main {
						dict<str, i32> d = ["a": 1]
						d[1..2]
					}
					"""),
				item => item.Code == "KRN2101").Code);

		// Шаг в срезе не поддержан — KRN2101.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics(
					"""
					fun main {
						list<i32> xs = [1, 2, 3]
						xs[0..2:1]
					}
					"""),
				item => item.Code == "KRN2101").Code);
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

		var catalog = Catalog();
		ModuleSymbol imported = NativeSymbolBuilder.Import(module, catalog, out var importDiagnostics);
		Assert.Empty(importDiagnostics);

		var bound = new Binder(imported, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);
		var generated = new CodeGenerator().Generate(bound);
		Assert.DoesNotContain(generated.Diagnostics, diagnostic => diagnostic.IsError);
		return generated;
	}

	private static BoundModule Bind(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		return new Binder(module, table).Bind();
	}

	private static IReadOnlyList<Diagnostic> Diagnostics(string source) => Bind(source).Diagnostics;
}
