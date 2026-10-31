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
/// B6b1: протоколы <c>hash</c>/<c>compare</c> (spec §9, Q23/Q24). У <c>unit</c>/<c>unit!</c>
/// hash и compare структурные по полям, у <c>type</c> hash — по идентичности ссылки;
/// объявленные <c>op hash</c>/<c>op compare</c> перекрывают дефолты. Порядок идёт через
/// compare, для <c>type</c> без <c>op compare</c> — KRN2101.
/// </summary>
public sealed class ProtocolTests
{
	[Fact]
	public void Default_unit_hash_is_structural()
	{
		const string source = """
			unit Point(i32 x, i32 y)

			fun same() -> bool { Point(1, 2).hash() == Point(1, 2).hash() }
			fun value() -> u64 { Point(1, 2).hash() }
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "same"));
		Assert.Equal(ValueKind.U64, Run(source, "value").Kind);
	}

	[Fact]
	public void Default_unit_compare_and_ordering_are_lexicographic()
	{
		const string source = """
			unit Point(i32 x, i32 y)

			fun less() -> bool { Point(1, 2) < Point(1, 3) }
			fun less_equal() -> bool { Point(1, 2) <= Point(1, 2) }
			fun greater() -> bool { Point(2, 0) > Point(1, 9) }
			fun cmp() -> i32 { Point(1, 2).compare(Point(1, 3)) }
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "less"));
		Assert.Equal(KValue.Bool(true), Run(source, "less_equal"));
		Assert.Equal(KValue.Bool(true), Run(source, "greater"));
		Assert.Equal(KValue.I32(-1), Run(source, "cmp"));
	}

	[Fact]
	public void Nested_unit_compare_and_hash_recurse()
	{
		const string source = """
			unit Point(i32 x, i32 y)
			unit Line(Point a, Point b)

			fun same_hash() -> bool {
				Line(Point(1, 2), Point(3, 4)).hash() == Line(Point(1, 2), Point(3, 4)).hash()
			}

			fun cmp() -> i32 {
				Line(Point(1, 2), Point(3, 4)).compare(Line(Point(1, 2), Point(3, 5)))
			}
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "same_hash"));
		Assert.Equal(KValue.I32(-1), Run(source, "cmp"));
	}

	[Fact]
	public void Declared_operators_override_defaults()
	{
		const string source = """
			unit Celsius(i32 degrees) {
				op hash() -> u64 { 42u64 }
				op compare(Celsius other) -> i32 { $degrees - other.degrees }
			}

			fun hash_value() -> u64 { Celsius(1).hash() }
			fun cmp() -> i32 { Celsius(2).compare(Celsius(1)) }
			fun less() -> bool { Celsius(1) < Celsius(2) }
			""";

		Assert.Equal(KValue.U64(42), Run(source, "hash_value"));
		Assert.Equal(KValue.I32(1), Run(source, "cmp"));
		Assert.Equal(KValue.Bool(true), Run(source, "less"));
	}

	[Fact]
	public void Class_hash_is_identity_and_ordering_needs_compare()
	{
		const string hashSource = """
			type Cell(i32 v)

			fun same_hash() -> bool {
				Cell c = Cell(1)
				c.hash() == c.hash()
			}
			""";
		Assert.Equal(KValue.Bool(true), Run(hashSource, "same_hash"));

		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics("""
					type Cell(i32 v)
					fun cmp() -> i32 { Cell(1).compare(Cell(2)) }
					"""),
				item => item.Code == "KRN2101").Code);

		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics("""
					type Cell(i32 v)
					fun order() -> bool { Cell(1) < Cell(2) }
					"""),
				item => item.Code == "KRN2101").Code);
	}

	[Fact]
	public void Non_hashable_and_non_comparable_units_report_KRN2101()
	{
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics("""
					unit Box(list<i32> items)
					fun h() -> u64 { Box([1]).hash() }
					"""),
				item => item.Code == "KRN2101").Code);

		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics("""
					unit Box(list<i32> items)
					fun cmp() -> i32 { Box([1]).compare(Box([2])) }
					"""),
				item => item.Code == "KRN2101").Code);

		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics("""
					unit Bad(i32 v) { op hash() -> i32 { 1 } }
					fun h() -> u64 { Bad(1).hash() }
					"""),
				item => item.Code == "KRN2101").Code);
	}

	[Fact]
	public void Extension_operator_form_still_parses()
	{
		var parsed = Parser.Parse("op i32.foo(i32 x) -> i32 { x }");

		Assert.Empty(parsed.Diagnostics);
		Assert.IsType<ExtensionOperatorDeclaration>(Assert.Single(parsed.Tree.Declarations));
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
