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
/// Подстановка типов нативных членов встроенных generic-типов (spec §12, §16):
/// <c>list&lt;T&gt;.first() -&gt; T</c>, <c>map -&gt; list&lt;U&gt;</c>,
/// <c>dict&lt;K, V&gt;.keys() -&gt; list&lt;K&gt;</c>. Мост C# описан через <c>KValue</c>
/// (<c>object</c>), поэтому статический тип подставляет binder, а не VM.
/// </summary>
public sealed class GenericNativeMemberTests
{
	[Fact]
	public void List_first_last_get_and_remove_at_are_element_typed()
	{
		const string source = """
			fun total() -> i32 {
				list<i32> xs = [10, 20, 30, 40]
				i32 a = xs.first()
				i32 b = xs.last()
				i32 c = xs.get(1)
				i32 d = xs.remove_at(0)
				a + b + c + d
			}

			fun after_remove() -> i32 {
				list<i32> xs = [10, 20, 30]
				i32 removed = xs.remove_at(1)
				xs.len * 100 + removed + xs[1]
			}
			""";

		Assert.Equal(KValue.I32(80), Run(source, "total"));
		Assert.Equal(KValue.I32(250), Run(source, "after_remove"));
	}

	[Fact]
	public void Map_returns_list_of_the_function_result()
	{
		const string source = """
			fun doubled() -> i32 {
				list<i32> xs = [1, 2, 3]
				list<i32> result = xs.map(fun(i32 x) -> i32 x * 2)
				result[2] + result.len
			}

			fun texts() -> str {
				list<i32> xs = [1, 2]
				list<str> result = xs.map(fun(i32 x) -> str f"n${x}")
				result.join(",")
			}

			fun first_text() -> str {
				list<i32> xs = [7, 8]
				list<str> result = xs.map(fun(i32 x) -> str f"${x}")
				result.first()
			}
			""";

		Assert.Equal(KValue.I32(9), Run(source, "doubled"));
		Assert.Equal(KValue.Str("n1,n2"), Run(source, "texts"));
		Assert.Equal(KValue.Str("7"), Run(source, "first_text"));
	}

	[Fact]
	public void Filter_returns_the_same_list_type_with_a_bool_function()
	{
		const string source = """
			fun evens() -> i32 {
				list<i32> xs = [1, 2, 3, 4]
				list<i32> result = xs.filter(fun(i32 x) -> bool x % 2 == 0)
				result.len * 10 + result[0] + result[1]
			}
			""";

		Assert.Equal(KValue.I32(26), Run(source, "evens"));
	}

	[Fact]
	public void Array_get_and_slice_are_element_typed()
	{
		const string source = """
			fun summ() -> i32 {
				array<i32> items = <i32>[5, 6, 7]
				i32 x = items.get(0) + items.get(2)
				list<i32> tail = items.slice(1, 3)
				x + tail.len * 10 + tail[0]
			}
			""";

		Assert.Equal(KValue.I32(38), Run(source, "summ"));
	}

	[Fact]
	public void Dict_get_keys_and_values_are_substituted()
	{
		const string source = """
			fun lookups() -> i32 {
				dict<str, i32> ages = ["a": 10, "b": 20]
				i32 sum = ages.get("a") + ages.get("b")
				list<str> keys = ages.keys()
				list<i32> values = ages.values()
				i32 bonus = if ages.contains_key("a") { 1 } else { 0 }
				sum + keys.len * 10 + values.len * 100 + bonus
			}

			fun first_key() -> str {
				dict<str, i32> ages = ["x": 1]
				list<str> keys = ages.keys()
				keys.first()
			}
			""";

		Assert.Equal(KValue.I32(251), Run(source, "lookups"));
		Assert.Equal(KValue.Str("x"), Run(source, "first_key"));
	}

	[Fact]
	public void Reverse_slice_and_join_keep_their_contracts()
	{
		const string source = """
			fun text() -> str {
				list<i32> xs = [3, 1, 2]
				list<i32> reversed = xs.reverse()
				list<i32> middle = reversed.slice(1, 3)
				middle.join(",")
			}

			fun first_of_reversed() -> i32 {
				list<i32> xs = [3, 1, 2]
				list<i32> reversed = xs.reverse()
				reversed.first()
			}
			""";

		Assert.Equal(KValue.Str("1,3"), Run(source, "text"));
		Assert.Equal(KValue.I32(2), Run(source, "first_of_reversed"));
	}

	[Fact]
	public void Wrong_map_or_filter_argument_reports_KRN2102()
	{
		var wrongMapShape = CompileWithDiagnostics("""
			fun main {
				list<i32> xs = [1, 2, 3]
				list<i32> bad = xs.map(fun(i32 x, i32 y) -> i32 x + y)
				println(bad.len)
			}
			""");
		var mapErrors = wrongMapShape.Diagnostics.Where(diagnostic => diagnostic.Code == "KRN2102").ToList();
		var mapError = Assert.Single(mapErrors);
		Assert.True(mapError.IsError);
		Assert.Contains("map", mapError.Message);

		var notAFunction = CompileWithDiagnostics("""
			fun main {
				list<i32> xs = [1, 2, 3]
				list<i32> bad = xs.map(5)
				println(bad.len)
			}
			""");
		Assert.Contains(notAFunction.Diagnostics, diagnostic => diagnostic.Code == "KRN2102" && diagnostic.IsError);

		var filterNotBool = CompileWithDiagnostics("""
			fun main {
				list<i32> xs = [1, 2, 3]
				list<i32> bad = xs.filter(fun(i32 x) -> i32 x)
				println(bad.len)
			}
			""");
		var filterErrors = filterNotBool.Diagnostics.Where(diagnostic => diagnostic.Code == "KRN2102").ToList();
		var filterError = Assert.Single(filterErrors);
		Assert.True(filterError.IsError);
		Assert.Contains("bool", filterError.Message);
	}

	[Fact]
	public void User_members_are_not_touched_by_the_native_table()
	{
		Assert.Equal(KValue.I32(8), Run("""
			unit Box(i32 value) {
				fun first() -> i32 { $value + 7 }
			}

			fun f() -> i32 {
				Box(1).first()
			}
			""", "f"));
	}

	[Fact]
	public void Generic_function_receiver_keeps_the_type_parameter()
	{
		const string source = """
			fun first_of<T>(list<T> xs) -> T xs.first()

			fun number() -> i32 first_of([1, 2, 3])
			fun text() -> str first_of(["a", "b"])
			""";

		Assert.Equal(KValue.I32(1), Run(source, "number"));
		Assert.Equal(KValue.Str("a"), Run(source, "text"));
	}

	[Fact]
	public void Dict_keys_and_values_work_as_properties()
	{
		const string source = """
			fun keys_len() -> i32 {
				dict<str, i32> d = ["a": 1, "b": 2]
				d.keys.len
			}

			fun values_len() -> i32 {
				dict<str, i32> d = ["a": 1, "b": 2]
				d.values.len
			}

			fun sum() -> i32 {
				dict<str, i32> d = ["a": 1, "b": 2]
				d.values.get(0) + d.values.get(1)
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "keys_len"));
		Assert.Equal(KValue.I32(2), Run(source, "values_len"));
		Assert.Equal(KValue.I32(3), Run(source, "sum"));
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
