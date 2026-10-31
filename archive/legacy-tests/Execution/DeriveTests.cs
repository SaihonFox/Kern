using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Bytecode;
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
/// B8a: <c>#[derive(hash, compare)]</c> (spec §7/§16). У <c>type</c> derive включает полевое
/// поведение протоколов: хэш по reified-типу и полям, лексикографический compare и порядок.
/// У <c>unit</c>/<c>unit!</c> хэш и сравнение уже структурные — derive принимается и ничего
/// не меняет. Ручной <c>op hash</c>/<c>op compare</c> или метод с тем же именем конфликтует
/// с derive (KRN2106), а неверная цель/имя атрибута — KRN2101.
/// </summary>
public sealed class DeriveTests
{
	[Fact]
	public void Class_derive_hash_uses_fields()
	{
		const string source = """
			#[derive(hash)] type Point(i32 x, i32 y)

			fun same_hash() -> bool { Point(1, 2).hash() == Point(1, 2).hash() }
			fun other_hash() -> bool { Point(1, 2).hash() != Point(1, 3).hash() }
			fun direct() -> u64 {
				Point p = Point(1, 2)
				p.hash()
			}
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "same_hash"));
		Assert.Equal(KValue.Bool(true), Run(source, "other_hash"));
		Assert.Equal(ValueKind.U64, Run(source, "direct").Kind);
	}

	[Fact]
	public void Class_derive_compare_is_lexicographic()
	{
		const string source = """
			#[derive(compare)] type Point(i32 x, i32 y)

			fun cmp_negative() -> i32 { Point(1, 2).compare(Point(1, 3)) }
			fun cmp_zero() -> i32 { Point(1, 2).compare(Point(1, 2)) }
			fun cmp_positive() -> i32 { Point(2, 0).compare(Point(1, 9)) }
			fun less() -> bool { Point(1, 2) < Point(1, 3) }
			fun less_equal() -> bool { Point(1, 2) <= Point(1, 2) }
			fun greater() -> bool { Point(2, 0) > Point(1, 9) }
			fun greater_equal() -> bool { Point(1, 2) >= Point(1, 2) }
			""";

		Assert.Equal(KValue.I32(-1), Run(source, "cmp_negative"));
		Assert.Equal(KValue.I32(0), Run(source, "cmp_zero"));
		Assert.Equal(KValue.I32(1), Run(source, "cmp_positive"));
		Assert.Equal(KValue.Bool(true), Run(source, "less"));
		Assert.Equal(KValue.Bool(true), Run(source, "less_equal"));
		Assert.Equal(KValue.Bool(true), Run(source, "greater"));
		Assert.Equal(KValue.Bool(true), Run(source, "greater_equal"));
	}

	[Fact]
	public void Class_with_both_derives()
	{
		const string source = """
			#[derive(hash, compare)] type Cell(i32 v)

			fun same_hash() -> bool { Cell(7).hash() == Cell(7).hash() }
			fun cmp() -> i32 { Cell(3).compare(Cell(4)) }
			fun less() -> bool { Cell(3) < Cell(4) }
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "same_hash"));
		Assert.Equal(KValue.I32(-1), Run(source, "cmp"));
		Assert.Equal(KValue.Bool(true), Run(source, "less"));
	}

	[Fact]
	public void Separate_derive_attributes_are_merged()
	{
		const string source = """
			#[derive(hash)]
			#[derive(compare)]
			type Pair(i32 a, i32 b)

			fun same_hash() -> bool { Pair(1, 2).hash() == Pair(1, 2).hash() }
			fun cmp() -> i32 { Pair(1, 2).compare(Pair(1, 3)) }
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "same_hash"));
		Assert.Equal(KValue.I32(-1), Run(source, "cmp"));
	}

	[Fact]
	public void Unit_and_immutable_unit_accept_derives()
	{
		const string source = """
			#[derive(hash, compare)] unit Point(i32 x, i32 y)
			#[derive(hash, compare)] unit! Pair(i32 a, i32 b)

			fun same_hash() -> bool { Point(1, 2).hash() == Point(1, 2).hash() }
			fun cmp() -> i32 { Point(1, 2).compare(Point(1, 3)) }
			fun less() -> bool { Point(1, 2) < Point(1, 3) }
			fun immutable_hash() -> bool { Pair(1, 2).hash() == Pair(1, 2).hash() }
			fun immutable_cmp() -> i32 { Pair(2, 0).compare(Pair(1, 9)) }
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "same_hash"));
		Assert.Equal(KValue.I32(-1), Run(source, "cmp"));
		Assert.Equal(KValue.Bool(true), Run(source, "less"));
		Assert.Equal(KValue.Bool(true), Run(source, "immutable_hash"));
		Assert.Equal(KValue.I32(1), Run(source, "immutable_cmp"));
	}

	[Fact]
	public void Hand_written_hash_operator_conflicts_with_derive()
	{
		var diagnostic = Assert.Single(
			Diagnostics("""
				#[derive(hash)] type Celsius(i32 degrees) {
					op hash() -> u64 { 42u64 }
				}
				"""),
			item => item.Code == "KRN2106");

		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
		Assert.Contains("hash", diagnostic.Message);
		Assert.Contains("derive", diagnostic.Message);
	}

	[Fact]
	public void Hand_written_compare_operator_conflicts_with_derive()
	{
		var diagnostic = Assert.Single(
			Diagnostics("""
				#[derive(compare)] type Celsius(i32 degrees) {
					op compare(Celsius other) -> i32 { $degrees - other.degrees }
				}
				"""),
			item => item.Code == "KRN2106");

		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
		Assert.Contains("compare", diagnostic.Message);
		Assert.Contains("derive", diagnostic.Message);
	}

	[Fact]
	public void Hand_written_compare_method_conflicts_with_derive()
	{
		var diagnostic = Assert.Single(
			Diagnostics("""
				#[derive(compare)] type Celsius(i32 degrees) {
					fun compare(Celsius other) -> i32 { $degrees - other.degrees }
				}
				"""),
			item => item.Code == "KRN2106");

		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
		Assert.Contains("compare", diagnostic.Message);
	}

	[Fact]
	public void Unknown_and_empty_derives_report_KRN2101()
	{
		var unknown = Assert.Single(
			CollectDiagnostics("""#[derive(to_str)] type Point(i32 x, i32 y)"""),
			item => item.Code == "KRN2101");
		Assert.Equal(DiagnosticSeverity.Error, unknown.Severity);
		Assert.Contains("to_str", unknown.Message);
		Assert.Contains("hash", unknown.Message);
		Assert.Contains("compare", unknown.Message);

		var empty = Assert.Single(
			CollectDiagnostics("""#[derive] type Point(i32 x, i32 y)"""),
			item => item.Code == "KRN2101");
		Assert.Contains("hash", empty.Message);
		Assert.Contains("compare", empty.Message);
	}

	[Fact]
	public void Derive_on_a_function_reports_KRN2101()
	{
		var diagnostic = Assert.Single(
			CollectDiagnostics("""#[derive(hash)] fun helper() -> i32 { 1 }"""),
			item => item.Code == "KRN2101");

		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
		Assert.Contains("type", diagnostic.Message);
		Assert.Contains("unit", diagnostic.Message);
	}

	[Fact]
	public void Derive_on_a_union_alias_reports_KRN2101()
	{
		var diagnostic = Assert.Single(
			CollectDiagnostics("""
				unit A
				unit B
				#[derive(hash)] type Letter = [A, B]
				"""),
			item => item.Code == "KRN2101");

		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
		Assert.Contains("union", diagnostic.Message);
	}

	[Fact]
	public void Derive_on_a_static_type_reports_KRN2101()
	{
		var diagnostic = Assert.Single(
			CollectDiagnostics("""
				type Outer {
					#[derive(hash)] #type Inner(i32 x)
				}
				"""),
			item => item.Code == "KRN2101");

		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
		Assert.Contains("статическ", diagnostic.Message);
	}

	[Fact]
	public void Duplicate_derive_protocols_report_KRN2106()
	{
		var repeatedName = Assert.Single(
			CollectDiagnostics("""#[derive(hash, hash)] type Point(i32 x, i32 y)"""),
			item => item.Code == "KRN2106");
		Assert.Equal(DiagnosticSeverity.Error, repeatedName.Severity);

		var repeatedAttribute = Assert.Single(
			CollectDiagnostics("""
				#[derive(hash)]
				#[derive(hash)]
				type Point(i32 x, i32 y)
				"""),
			item => item.Code == "KRN2106");
		Assert.Equal(DiagnosticSeverity.Error, repeatedAttribute.Severity);
	}

	[Fact]
	public void Valid_derive_produces_no_KRN2307()
	{
		var diagnostics = CollectDiagnostics("""#[derive(hash, compare)] type Point(i32 x, i32 y)""");

		Assert.DoesNotContain(diagnostics, item => item.Code == "KRN2307");
		Assert.Empty(diagnostics);
	}

	[Fact]
	public void Derived_class_survives_kbc_round_trip()
	{
		const string source = """
			#[derive(hash, compare)] type Point(i32 x, i32 y)

			fun same_hash() -> bool { Point(1, 2).hash() == Point(1, 2).hash() }
			fun less() -> bool { Point(1, 2) < Point(1, 3) }
			fun cmp() -> i32 { Point(2, 0).compare(Point(1, 9)) }
			""";

		var result = Compile(source);
		KbcModule restored = KbcReader.Read(KbcWriter.Write(result.Module));
		var interpreter = new Interpreter(restored, new NativeResolver(Catalog()));

		Assert.Equal(KValue.Bool(true), interpreter.Run("same_hash", []));
		Assert.Equal(KValue.Bool(true), interpreter.Run("less", []));
		Assert.Equal(KValue.I32(1), interpreter.Run("cmp", []));
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

	private static IReadOnlyList<Diagnostic> CollectDiagnostics(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (_, diagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		return diagnostics;
	}
}
