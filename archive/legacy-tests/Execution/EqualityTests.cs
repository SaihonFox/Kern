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
/// B6a: равенство <c>==</c>/<c>!=</c> (spec §9). У <c>unit</c>/<c>unit!</c> оно структурное
/// по полям, у <c>type</c> — по идентичности ссылки; исполняет его VM. Здесь же — проверка
/// хэшируемости ключа словаря: примитивы/str/класс/unit с хэшируемыми полями.
/// </summary>
public sealed class EqualityTests
{
	[Fact]
	public void Unit_equality_compares_fields()
	{
		const string source = """
			unit Point(i32 x, i32 y)
			unit! Frozen(i32 x)

			fun same() -> bool { Point(1, 2) == Point(1, 2) }

			fun different() -> bool { Point(1, 2) == Point(1, 3) }

			fun not_equal() -> bool { Point(1, 2) != Point(1, 3) }

			fun immutable_same() -> bool { Frozen(1) == Frozen(1) }

			fun immutable_different() -> bool { Frozen(1) == Frozen(2) }
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "same"));
		Assert.Equal(KValue.Bool(false), Run(source, "different"));
		Assert.Equal(KValue.Bool(true), Run(source, "not_equal"));
		Assert.Equal(KValue.Bool(true), Run(source, "immutable_same"));
		Assert.Equal(KValue.Bool(false), Run(source, "immutable_different"));
	}

	[Fact]
	public void Nested_unit_equality_is_structural()
	{
		const string source = """
			unit Point(i32 x, i32 y)
			unit Line(Point a, Point b)

			fun same() -> bool {
				Line(Point(0, 0), Point(1, 1)) == Line(Point(0, 0), Point(1, 1))
			}

			fun different() -> bool {
				Line(Point(0, 0), Point(1, 1)) == Line(Point(0, 0), Point(1, 2))
			}
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "same"));
		Assert.Equal(KValue.Bool(false), Run(source, "different"));
	}

	[Fact]
	public void Class_equality_uses_reference_identity()
	{
		const string source = """
			type Cell(i32 value)

			fun same() -> bool {
				Cell a = Cell(1)
				a == a
			}

			fun different() -> bool {
				Cell a = Cell(1)
				Cell b = Cell(1)
				a == b
			}
			""";

		Assert.Equal(KValue.Bool(true), Run(source, "same"));
		Assert.Equal(KValue.Bool(false), Run(source, "different"));
	}

	[Fact]
	public void Unit_key_in_dict_literal_is_found_by_a_fresh_equal_key()
	{
		const string source = """
			unit Point(i32 x, i32 y)

			fun find() -> str {
				dict<Point, str> d = [Point(1, 2): "a"]
				d[Point(1, 2)]
			}
			""";

		Assert.Equal(KValue.Str("a"), Run(source, "find"));
	}

	[Fact]
	public void Non_hashable_keys_report_KRN2101()
	{
		// Разные unit-типы несравнимы: обычная несовместимость KRN2101.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics("""
					unit Point(i32 x)
					unit Other(i32 x)

					fun same() -> bool { Point(1) == Other(1) }
					"""),
				item => item.Code == "KRN2101").Code);

		// list в роли ключа словаря не хэшируется.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics("""fun main { dict<list<i32>, str> d = [[1]: "a"] }"""),
				item => item.Code == "KRN2101").Code);

		// unit хэшируем только при хэшируемых полях: list внутри Box запрещает ключ.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics("""
					unit Box(list<i32> items)

					fun main { dict<Box, str> d = [Box([1]): "a"] }
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
