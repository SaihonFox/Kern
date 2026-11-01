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
/// Нативный generic-тип <c>set&lt;T&gt;</c> (spec §16): фабрика <c>set&lt;T&gt;()</c>,
/// члены <c>add/remove/contains/clear/len/is_empty/to_list</c>, <c>in</c>/<c>!in</c>.
/// </summary>
public sealed class SetTests
{
	[Fact]
	public void Add_collapses_duplicates_and_len_counts_unique_items()
	{
		const string source = """
			fun value() -> i32 {
				set<i32> s()
				s.add(3)
				s.add(3)
				s.add(7)
				s.len
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "value"));
	}

	[Fact]
	public void Contains_and_membership_use_element_type()
	{
		const string source = """
			fun contains() -> bool {
				set<i32> s()
				s.add(7)
				s.contains(7)
			}

			fun member() -> bool {
				set<i32> s()
				s.add(3)
				3 in s
			}

			fun not_member() -> bool {
				set<i32> s()
				s.add(3)
				5 !in s
			}
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "contains"));
		Assert.Equal(KValue.Bool(true), Run(source, "member"));
		Assert.Equal(KValue.Bool(true), Run(source, "not_member"));
	}

	[Fact]
	public void Remove_and_clear_change_the_set()
	{
		const string source = """
			fun removed() -> bool {
				set<i32> s()
				s.add(1)
				s.remove(1)
			}

			fun after_remove() -> i32 {
				set<i32> s()
				s.add(1)
				s.add(2)
				s.remove(1)
				s.len
			}

			fun cleared() -> i32 {
				set<i32> s()
				s.add(1)
				s.clear()
				s.len
			}
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "removed"));
		Assert.Equal(KValue.I32(1), Run(source, "after_remove"));
		Assert.Equal(KValue.I32(0), Run(source, "cleared"));
	}

	[Fact]
	public void Empty_set_is_empty_and_to_list_is_a_copy()
	{
		const string source = """
			fun empty() -> bool {
				set<i32> s()
				s.is_empty
			}

			fun list_len() -> i32 {
				set<str> s()
				s.add("a")
				s.add("b")
				list<str> items = s.to_list()
				items.len
			}
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "empty"));
		Assert.Equal(KValue.I32(2), Run(source, "list_len"));
	}

	[Fact]
	public void Set_works_with_string_elements()
	{
		const string source = """
			fun contains() -> bool {
				set<str> s()
				s.add("a")
				s.contains("a")
			}

			fun missing() -> bool {
				set<str> s()
				s.add("a")
				"b" in s
			}
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "contains"));
		Assert.Equal(KValue.Bool(false), Run(source, "missing"));
	}

	[Fact]
	public void Removing_a_missing_item_returns_false()
	{
		const string source = """
			fun value() -> bool {
				set<i32> s()
				s.add(1)
				s.remove(2)
			}
			""";

		Assert.Equal(KValue.Bool(false), Run(source, "value"));
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
