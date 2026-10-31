using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

/// <summary>
/// B5b1: встроенная индексация коллекций (spec §3, round 27). <c>list&lt;T&gt;</c> и
/// <c>array&lt;T&gt;</c> читаются по индексу <c>i32</c> (отрицательный считается с конца),
/// <c>dict&lt;K, V&gt;</c> — по ключу; выход за границы и отсутствующий ключ дают панику.
/// Индексация <c>str</c> и срезы (B5b2) проверяются в <c>SliceTests</c>.
/// </summary>
public sealed class BuiltInIndexingTests
{
	[Fact]
	public void List_index_reads_forward_and_from_the_end()
	{
		const string source = """
			fun first() -> i32 {
				list<i32> xs = [10, 20, 30]
				xs[0]
			}

			fun third() -> i32 {
				list<i32> xs = [10, 20, 30]
				xs[2]
			}

			fun last() -> i32 {
				list<i32> xs = [10, 20, 30]
				xs[-1]
			}

			fun first_from_end() -> i32 {
				list<i32> xs = [10, 20, 30]
				xs[-3]
			}
			""";

		Assert.Equal(KValue.I32(10), Run(source, "first"));
		Assert.Equal(KValue.I32(30), Run(source, "third"));
		Assert.Equal(KValue.I32(30), Run(source, "last"));
		Assert.Equal(KValue.I32(10), Run(source, "first_from_end"));
	}

	[Fact]
	public void Array_index_reads_forward_and_from_the_end()
	{
		const string source = """
			fun second() -> i32 {
				array<i32> a = <i32>[1, 2, 3]
				a[1]
			}

			fun last() -> i32 {
				array<i32> a = <i32>[1, 2, 3]
				a[-1]
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "second"));
		Assert.Equal(KValue.I32(3), Run(source, "last"));
	}

	[Fact]
	public void Dict_index_reads_by_literal_and_variable_key()
	{
		const string source = """
			fun literal() -> i32 {
				dict<str, i32> d = ["a": 1, "b": 2]
				d["a"]
			}

			fun variable() -> i32 {
				dict<str, i32> d = ["a": 1, "b": 2]
				str k = "b"
				d[k]
			}
			""";

		Assert.Equal(KValue.I32(1), Run(source, "literal"));
		Assert.Equal(KValue.I32(2), Run(source, "variable"));
	}

	[Fact]
	public void Out_of_range_and_missing_key_panic()
	{
		// Индекс за длиной, слишком отрицательный индекс и отсутствующий ключ — runtime panic.
		const string source = """
			fun past_the_end() -> i32 {
				list<i32> xs = [1, 2, 3]
				xs[3]
			}

			fun too_negative() -> i32 {
				list<i32> xs = [1, 2, 3]
				xs[-4]
			}

			fun missing_key() -> i32 {
				dict<str, i32> d = ["a": 1]
				d["missing"]
			}
			""";

		Assert.Throws<KernPanic>(() => Run(source, "past_the_end"));
		Assert.Throws<KernPanic>(() => Run(source, "too_negative"));
		Assert.Throws<KernPanic>(() => Run(source, "missing_key"));
	}

	[Fact]
	public void Unsupported_index_forms_report_binder_errors()
	{
		// Два индекса у list — KRN2101: встроенное правило принимает ровно один.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics(
					"""
					fun main {
						list<i32> xs = [1, 2]
						xs[1, 2]
					}
					"""),
				item => item.Code == "KRN2101").Code);

		// str-индекс не приводится к i32 — KRN2102 из Convert.
		Assert.Equal(
			"KRN2102",
			Assert.Single(
				Diagnostics(
					"""
					fun main {
						list<i32> xs = [1, 2]
						xs["a"]
					}
					"""),
				item => item.Code == "KRN2102").Code);

		// Индексация str принимает ровно один индекс — лишний даёт KRN2101.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics(
					"""
					fun main {
						str s = "ab"
						s[0, 1]
					}
					"""),
				item => item.Code == "KRN2101").Code);

		// Срез с шагом остаётся недопустимым (B5b2 поддержал только `start..end`).
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
