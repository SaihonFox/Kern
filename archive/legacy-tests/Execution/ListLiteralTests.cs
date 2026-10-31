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
/// B3a/B3b1: литералы коллекций (spec §3). Неявный <c>[a, ..b, c]</c> проверяется сквозным
/// путём компилятора и VM: порядок элементов, spread без алиасинга, вложенные литералы и
/// диагностика пустого <c>[]</c>. Явный <c>&lt;i32&gt;[...]</c> фиксирует тип элемента, а
/// ожидаемый <c>array&lt;T&gt;</c> делает литерал массивом.
/// </summary>
public sealed class ListLiteralTests
{
	[Fact]
	public void List_literal_keeps_order_and_supports_len_first_last()
	{
		// `first`/`last` — нативные члены встроенного `list`, их возврат статически `object`,
		// поэтому значения забираются из функции с типом `object` и сравниваются как KValue.
		const string source = """
			fun length() -> i32 {
				list<i32> xs = [1, 2, 3]
				xs.len
			}

			fun head() -> object {
				list<i32> xs = [1, 2, 3]
				xs.first()
			}

			fun tail() -> object {
				list<i32> xs = [1, 2, 3]
				xs.last()
			}
			""";

		Assert.Equal(KValue.I32(3), Run(source, "length"));
		Assert.Equal(KValue.I32(1), Run(source, "head"));
		Assert.Equal(KValue.I32(3), Run(source, "tail"));
	}

	[Fact]
	public void Spread_copies_elements_without_aliasing_the_source()
	{
		const string source = """
			fun source_len() -> i32 {
				list<i32> a = [1, 2]
				list<i32> b = [..a, 3]
				a.len
			}

			fun result_len() -> i32 {
				list<i32> a = [1, 2]
				list<i32> b = [..a, 3]
				b.len
			}

			fun result_last() -> object {
				list<i32> a = [1, 2]
				list<i32> b = [..a, 3]
				b.last()
			}
			""";

		// Если бы spread делил хранилище, длина источника тоже стала бы 3.
		Assert.Equal(KValue.I32(2), Run(source, "source_len"));
		Assert.Equal(KValue.I32(3), Run(source, "result_len"));
		Assert.Equal(KValue.I32(3), Run(source, "result_last"));
	}

	[Fact]
	public void Nested_literals_produce_nested_lists()
	{
		// `m.first()` статически возвращает `object`, поэтому внутренние длины проверяются
		// по реальным KList из результата, а `m.len` — через native-свойство.
		const string source = """
			fun outer_len() -> i32 {
				list<list<i32>> m = [[1], [2, 3]]
				m.len
			}

			fun matrix() -> list<list<i32>> {
				list<list<i32>> m = [[1], [2, 3]]
				m
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "outer_len"));

		var matrix = Assert.IsType<KList>(Run(source, "matrix").Ref);
		Assert.Equal(2, matrix.Count);

		var first = Assert.IsType<KList>(matrix[0].Ref);
		var last = Assert.IsType<KList>(matrix[1].Ref);
		Assert.Equal(1, first.Count);
		Assert.Equal(KValue.I32(1), first[0]);
		Assert.Equal(2, last.Count);
		Assert.Equal(KValue.I32(2), last[0]);
		Assert.Equal(KValue.I32(3), last[1]);
	}

	[Fact]
	public void Empty_literal_in_local_declaration_reports_KRN2102()
	{
		var diagnostics = Diagnostics(
			"""
			fun main {
				list<i32> xs = []
			}
			""");

		var diagnostic = Assert.Single(diagnostics, item => item.Code == "KRN2102");
		Assert.True(diagnostic.IsError);
	}

	[Fact]
	public void Untyped_mixed_integral_and_float_literal_reports_KRN2101()
	{
		// spec §3: смешивание inum и fnum без явного типа — ошибка, а не расширение до f64.
		var diagnostic = Assert.Single(
			Diagnostics("fun main { var xs = [1, 2.5] }"),
			item => item.Code == "KRN2101");

		Assert.True(diagnostic.IsError);
		Assert.Contains("i32", diagnostic.Message);
		Assert.Contains("f64", diagnostic.Message);
		Assert.Contains("<f64>[...]", diagnostic.Message);
	}

	[Fact]
	public void Untyped_integer_literal_binds_to_list_i32()
	{
		var local = BindLocal("fun f { var xs = [1, 2] }");

		Assert.Equal(ListType(PrimitiveType.I32), local.Type);
	}

	[Fact]
	public void Untyped_integral_widening_binds_to_list_i64()
	{
		// Расширение внутри целого семейства разрешено: i64 + i32 → i64.
		var local = BindLocal("fun f { var xs = [1i64, 2] }");

		Assert.Equal(ListType(PrimitiveType.I64), local.Type);
	}

	[Fact]
	public void Untyped_string_literal_binds_to_list_str()
	{
		var local = BindLocal("""fun f { var xs = ["a", "b"] }""");

		Assert.Equal(ListType(PrimitiveType.Str), local.Type);
	}

	[Fact]
	public void Untyped_bool_literal_binds_to_list_bool()
	{
		var local = BindLocal("fun f { var xs = [true, false] }");

		Assert.Equal(ListType(PrimitiveType.Bool), local.Type);
	}

	[Fact]
	public void Spread_of_non_collection_reports_KRN2101()
	{
		var diagnostic = Assert.Single(
			Diagnostics("fun main { var xs = [..1] }"),
			item => item.Code == "KRN2101");

		Assert.True(diagnostic.IsError);
	}

	[Fact]
	public void Nested_literal_type_mismatch_reports_KRN2101()
	{
		// list<i32> и list<str> не имеют общего типа элемента: смешивать вложенные неявные
		// литералы без явного типа нельзя.
		var diagnostic = Assert.Single(
			Diagnostics("""fun main { var xs = [[1], ["a"]] }"""),
			item => item.Code == "KRN2101");

		Assert.True(diagnostic.IsError);
		Assert.Contains("list<i32>", diagnostic.Message);
		Assert.Contains("list<str>", diagnostic.Message);
	}

	[Fact]
	public void Explicit_i64_hint_widens_integer_literals()
	{
		// Подсказка list<i64> преобразует литералы 1 и 2 к i64 (spec §3, §15).
		const string source = """
			fun length() -> i32 {
				list<i64> xs = [1, 2]
				xs.len
			}

			fun last() -> object {
				list<i64> xs = [1, 2]
				xs.last()
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "length"));
		Assert.Equal(KValue.I64(2), Run(source, "last"));
	}

	[Fact]
	public void Explicit_f64_hint_accepts_mixed_literals()
	{
		// Явный list<f64> снимает запрет смешивания: целый литерал расширяется до f64.
		const string source = """
			fun length() -> i32 {
				list<f64> xs = [1, 2.5]
				xs.len
			}

			fun last() -> object {
				list<f64> xs = [1, 2.5]
				xs.last()
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "length"));
		Assert.Equal(KValue.F64(2.5), Run(source, "last"));
	}

	[Fact]
	public void Spread_of_list_then_element_appends_without_touching_source()
	{
		// array<T> пока нельзя создать в Kern, поэтому spread проверяется на list<i32>:
		// элементы копируются, последний добавляется в конец.
		const string source = """
			fun source_len() -> i32 {
				list<i32> arr = [1, 2, 3]
				list<i32> xs = [..arr, 4]
				arr.len
			}

			fun result_len() -> i32 {
				list<i32> arr = [1, 2, 3]
				list<i32> xs = [..arr, 4]
				xs.len
			}

			fun result_last() -> object {
				list<i32> arr = [1, 2, 3]
				list<i32> xs = [..arr, 4]
				xs.last()
			}
			""";

		Assert.Equal(KValue.I32(3), Run(source, "source_len"));
		Assert.Equal(KValue.I32(4), Run(source, "result_len"));
		Assert.Equal(KValue.I32(4), Run(source, "result_last"));
	}

	[Fact]
	public void Untyped_string_literal_list_has_len_two()
	{
		const string source = """
			fun values() -> list<str> {
				var xs = ["a", "b"]
				xs
			}
			""";

		var list = Assert.IsType<KList>(Run(source, "values").Ref);
		Assert.Equal(2, list.Count);
		Assert.Equal(KValue.Str("a"), list[0]);
		Assert.Equal(KValue.Str("b"), list[1]);
	}

	[Fact]
	public void Untyped_bool_literal_list_has_len_two()
	{
		const string source = """
			fun values() -> list<bool> {
				var xs = [true, false]
				xs
			}
			""";

		var list = Assert.IsType<KList>(Run(source, "values").Ref);
		Assert.Equal(2, list.Count);
		Assert.Equal(KValue.Bool(true), list[0]);
		Assert.Equal(KValue.Bool(false), list[1]);
	}

	[Fact]
	public void Typed_list_literal_returns_list_i32()
	{
		// <i32>[...] без array-подсказки — обычный list<i32> (spec §3).
		const string source = """
			fun values() -> list<i32> {
				<i32>[1, 2, 3]
			}
			""";

		var list = Assert.IsType<KList>(Run(source, "values").Ref);
		Assert.Equal(3, list.Count);
		Assert.Equal(KValue.I32(1), list[0]);
		Assert.Equal(KValue.I32(2), list[1]);
		Assert.Equal(KValue.I32(3), list[2]);
	}

	[Fact]
	public void Typed_literal_with_array_hint_builds_array()
	{
		// Ожидаемый array<i32> выбирает array-вид литерала; NewArray снимает элементы по порядку.
		const string source = """
			fun values() -> array<i32> {
				<i32>[1, 2, 3]
			}
			""";

		var array = Assert.IsType<KArray>(Run(source, "values").Ref);
		Assert.Equal(3, array.Length);
		Assert.Equal(KValue.I32(1), array[0]);
		Assert.Equal(KValue.I32(2), array[1]);
		Assert.Equal(KValue.I32(3), array[2]);
	}

	[Fact]
	public void Array_typed_literal_matches_list_pattern()
	{
		// array<i32> a = <i32>[...] и списковой паттерн по массиву (B4b1) вместе.
		const string source = """
			fun sum() -> i32 {
				array<i32> a = <i32>[1, 2, 3]
				match a { [x, y, z] => x + y + z; _ => -1 }
			}
			""";

		Assert.Equal(KValue.I32(6), Run(source, "sum"));
	}

	[Fact]
	public void Explicit_empty_literal_builds_empty_list_and_untyped_empty_still_reports_KRN2102()
	{
		// <i32>[] допустим: явный тип фиксирует T, вид по умолчанию — list.
		const string source = """
			fun empty() -> list<i32> {
				<i32>[]
			}
			""";

		var list = Assert.IsType<KList>(Run(source, "empty").Ref);
		Assert.Equal(0, list.Count);

		// Неявный [] без явного типа по-прежнему KRN2102 (Q21 открыт).
		var diagnostic = Assert.Single(
			Diagnostics("fun main { list<i32> xs = [] }"),
			item => item.Code == "KRN2102");
		Assert.True(diagnostic.IsError);
	}

	[Fact]
	public void Typed_literal_mismatches_report_KRN2101()
	{
		// list -> array неявно не преобразуется: это обычная несовместимость KRN2101.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics("fun main { array<i32> a = [1, 2] }"),
				item => item.Code == "KRN2101").Code);

		// Spread внутри array-литерала не поддержан: NewArray не умеет добавлять после создания.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics("fun main { array<i32> a = <i32>[..[1]] }"),
				item => item.Code == "KRN2101").Code);

		// Явный тип элемента не совпал с типом переменной.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics("fun main { list<str> xs = <i32>[1] }"),
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

	private static BoundLocalDeclaration BindLocal(string source)
	{
		var module = Bind(source);
		var function = Assert.Single(module.Functions);
		return Assert.IsType<BoundLocalDeclaration>(function.Body.Statements[0]);
	}

	private static BuiltinGenericType ListType(KernType element) =>
		new(BuiltinGenericKind.List, [element]);

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
