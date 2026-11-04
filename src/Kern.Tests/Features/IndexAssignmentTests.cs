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
/// Запись по встроенному индексу (spec §5): <c>d[k] = v</c> и <c>xs[i] = v</c> для
/// <c>list</c>/<c>array</c>/<c>dict</c>, отрицательный индекс, паника вне границ,
/// а также KRN2106 для <c>str</c> и пользовательского <c>op []</c> (только чтение).
/// </summary>
public sealed class IndexAssignmentTests
{
	[Fact]
	public void Dict_store_inserts_updates_and_keeps_duplicate_keys_single()
	{
		Assert.Equal(KValue.I32(14), Run("""
			fun f() -> i32 {
				dict<str, i32> d = ["a": 1]
				d["b"] = 2
				d["a"] = 10
				d["a"] + d["b"] + d.len
			}
			""", "f"));
	}

	[Fact]
	public void List_store_writes_positive_and_negative_indices()
	{
		Assert.Equal(KValue.I32(40), Run("""
			fun f() -> i32 {
				list<i32> xs = [1, 2, 3]
				xs[0] = 10
				xs[-1] = 30
				xs[0] + xs[2]
			}
			""", "f"));
	}

	[Fact]
	public void Array_store_writes_elements()
	{
		Assert.Equal(KValue.I32(7), Run("""
			fun f() -> i32 {
				array<i32> a = <i32>[1, 2, 3]
				a[1] = 7
				a[1]
			}
			""", "f"));
	}

	[Fact]
	public void Compound_index_store_reads_then_writes()
	{
		Assert.Equal(KValue.I32(12), Run("""
			fun f() -> i32 {
				list<i32> xs = [1, 2, 3]
				xs[1] += 10
				xs[1]
			}
			""", "f"));
	}

	[Fact]
	public void Out_of_range_list_store_panics()
	{
		const string source = """
			fun past_the_end() {
				list<i32> xs = [1, 2, 3]
				xs[3] = 9
			}

			fun too_negative() {
				list<i32> xs = [1, 2, 3]
				xs[-4] = 9
			}
			""";

		Assert.Throws<KernPanic>(() => Run(source, "past_the_end"));
		Assert.Throws<KernPanic>(() => Run(source, "too_negative"));
	}

	[Fact]
	public void Str_store_is_rejected_as_read_only()
	{
		var diagnostic = Assert.Single(
			CompileWithDiagnostics("""
				fun f() {
					str s = "abc"
					s[0] = 'x'
				}
				""").Diagnostics,
			item => item.Code == "KRN2106");
		Assert.True(diagnostic.IsError);
		Assert.Contains("только для чтения", diagnostic.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Extension_indexer_reads_and_writes()
	{
		Assert.Equal(KValue.I32(5), Run("""
			unit Box(i32 n)
			op Box.[](i32 i) -> i32 $n + i
			op Box.[]=(i32 i, i32 v) { $n = v }

			fun f() -> i32 {
				Box b = Box(1)
				b[0] = 5
				b[0]
			}
			""", "f"));
	}

	[Fact]
	public void User_indexer_setter_writes_through_op_index_set()
	{
		Assert.Equal(KValue.I32(5), Run("""
			type Box(i32 value) {
				op [](i32 i) -> i32 {
					if i == 0 { ret $value }
					0
				}
				op []=(i32 i, i32 v) {
					if i == 0 { $value = v }
				}
			}

			fun f() -> i32 {
				Box b = Box(1)
				b[0] = 5
				b[0]
			}
			""", "f"));
	}

	[Fact]
	public void User_indexer_compound_assignment_reads_then_writes()
	{
		Assert.Equal(KValue.I32(7), Run("""
			type Box(i32 value) {
				op [](i32 i) -> i32 $value
				op []=(i32 i, i32 v) { $value = v }
			}

			fun f() -> i32 {
				Box b = Box(1)
				b[0] += 6
				b[0]
			}
			""", "f"));
	}

	[Fact]
	public void User_indexer_without_setter_is_rejected()
	{
		var diagnostic = Assert.Single(
			CompileWithDiagnostics("""
				unit Pair(i32 x, i32 y) {
					op [](i32 i) -> i32 { match i { 0 => $x; _ => $y } }
				}

				fun f() {
					Pair p = Pair(3, 4)
					p[0] = 5
				}
				""").Diagnostics,
			item => item.Code == "KRN2106");
		Assert.True(diagnostic.IsError);
		Assert.Contains("запись по индексу", diagnostic.Message, StringComparison.Ordinal);
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
