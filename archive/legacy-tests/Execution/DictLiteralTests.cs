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
/// B3b2: литералы словаря (spec §3). Неявный <c>["k": 1]</c> выводит <c>dict&lt;K, V&gt;</c>
/// объединением типов ключей и значений, <c>&lt;K: V&gt;[...]</c> фиксирует типы, а
/// <c>&lt;K: V&gt;[]</c> даёт пустой словарь. Проверяется сквозной путь компилятора и VM,
/// включая подсказку ожидаемого типа и диагностику несовместимых пар.
/// </summary>
public sealed class DictLiteralTests
{
	[Fact]
	public void Untyped_dict_literal_builds_dict_str_i32()
	{
		const string source = """
			fun values() -> dict<str, i32> {
				["a": 1, "b": 2]
			}
			""";

		var dict = Assert.IsType<KDict>(Run(source, "values").Ref);
		Assert.Equal(2, dict.Count);
		Assert.Equal(KValue.I32(1), dict.Get(KValue.Str("a")));
		Assert.Equal(KValue.I32(2), dict.Get(KValue.Str("b")));
	}

	[Fact]
	public void Typed_dict_literal_uses_explicit_key_and_value_types()
	{
		// <str: i32>[...] фиксирует K и V; результат — dict<str, i32> с одной парой.
		const string source = """
			fun values() -> dict<str, i32> {
				<str: i32>["a": 1]
			}
			""";

		var dict = Assert.IsType<KDict>(Run(source, "values").Ref);
		Assert.Equal(1, dict.Count);
		Assert.Equal(KValue.I32(1), dict.Get(KValue.Str("a")));
	}

	[Fact]
	public void Expected_dict_hint_widens_value_to_i64()
	{
		// Ожидаемый dict<str, i64> расширяет значение-литерал i32 до i64 (spec §3, §15).
		const string source = """
			fun values() -> dict<str, i64> {
				dict<str, i64> d = ["a": 1]
				d
			}
			""";

		var dict = Assert.IsType<KDict>(Run(source, "values").Ref);
		Assert.True(dict.TryGet(KValue.Str("a"), out KValue value));
		Assert.Equal(ValueKind.I64, value.Kind);
		Assert.Equal(KValue.I64(1), value);
	}

	[Fact]
	public void Typed_empty_dict_literal_builds_empty_dict()
	{
		// <K: V>[] допустим: типы заданы явно, пар нет.
		const string source = """
			fun empty() -> dict<str, i32> {
				<str: i32>[]
			}
			""";

		var dict = Assert.IsType<KDict>(Run(source, "empty").Ref);
		Assert.Equal(0, dict.Count);
	}

	[Fact]
	public void Dict_literal_mismatches_report_KRN2101()
	{
		// Ожидаемый list для словарного литерала — обычная несовместимость, не преобразование.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics("""fun main { list<i32> xs = ["a": 1] }"""),
				item => item.Code == "KRN2101").Code);

		// Ключ не приводится к K из ожидаемого dict<str, i32>.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics("fun main { dict<str, i32> d = [1: 2] }"),
				item => item.Code == "KRN2101").Code);

		// Значения не объединяются: str и bool без явного типа.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics("""fun main { var d = [1: "a", 2: true] }"""),
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
