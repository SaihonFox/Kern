using Kern.Compiler.CodeGen;
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
/// B4b1/B4b2: паттерны списка <c>[p1, p2]</c> / <c>[a, ..rest, z]</c> в match: проверка длины,
/// поэлементные паттерны, пустой <c>[]</c>, rest-паттерны и диагностики субъекта/повторного rest'а.
/// </summary>
public sealed class MatchListPatternTests
{
	[Fact]
	public void Fixed_length_list_pattern_binds_elements()
	{
		const string source =
			"""
			fun two() -> i32 {
				list<i32> xs = [3, 4]
				match xs { [a, b] => a + b; _ => 0 }
			}

			fun three() -> i32 {
				list<i32> xs = [3, 4, 5]
				match xs { [a, b] => a + b; _ => 0 }
			}

			fun arrayTwo(array<i32> xs) -> i32 {
				match xs { [a, b] => a + b; _ => 0 }
			}
			""";

		AssertI32(7, RunSource(source, "two"), "[3, 4] должно связать a=3, b=4");
		AssertI32(0, RunSource(source, "three"), "список из 3 элементов не совпадает с [a, b]");
		AssertI32(
			7,
			RunSource(source, "arrayTwo", KValue.Obj(new KArray(null, [KValue.I32(3), KValue.I32(4)]))),
			"паттерн списка должен работать и для array<i32>");
		AssertI32(
			0,
			RunSource(source, "arrayTwo", KValue.Obj(new KArray(null, [KValue.I32(3)]))),
			"массив из 1 элемента не совпадает с [a, b]");
	}

	[Fact]
	public void Empty_list_pattern_matches_only_empty_list()
	{
		const string source =
			"""
			fun f(list<i32> xs) -> i32 {
				match xs { [] => 1; _ => 0 }
			}
			""";

		AssertI32(1, RunSource(source, "f", KValue.Obj(new KList())), "[] должно совпасть с пустым списком");
		AssertI32(
			0,
			RunSource(source, "f", KValue.Obj(new KList(null, [KValue.I32(1)]))),
			"непустой список должен уйти в ветвь '_'");
	}

	[Fact]
	public void Element_patterns_support_literals_wildcards_and_ranges()
	{
		const string source =
			"""
			fun literalMatch() -> i32 {
				match [1, 9] { [1, _] => 1; _ => 0 }
			}

			fun literalMiss() -> i32 {
				match [2, 9] { [1, _] => 1; _ => 0 }
			}

			fun rangeMatch() -> i32 {
				match [2, 0] { [1..=3, _] => 1; _ => 0 }
			}
			""";

		AssertI32(1, RunSource(source, "literalMatch"), "[1, _] должно совпасть с [1, 9]");
		AssertI32(0, RunSource(source, "literalMiss"), "[1, _] не должно совпасть с [2, 9]");
		AssertI32(1, RunSource(source, "rangeMatch"), "[1..=3, _] должно совпасть с [2, 0]");
	}

	[Fact]
	public void Trailing_unnamed_rest_matches_any_tail()
	{
		const string source =
			"""
			fun head(list<i32> xs) -> i32 {
				match xs { [first, ..] => first; _ => -1 }
			}
			""";

		AssertI32(7, RunSource(source, "head", List(7)), "[first, ..] связывает единственный элемент");
		AssertI32(7, RunSource(source, "head", List(7, 8, 9)), "[first, ..] игнорирует хвост");
		AssertI32(-1, RunSource(source, "head", KValue.Obj(new KList())), "[first, ..] не совпадает с []");
	}

	[Fact]
	public void Named_rest_binds_tail_as_list_copy()
	{
		const string source =
			"""
			fun head(list<i32> xs) -> i32 {
				match xs { [a, ..rest] => a; _ => -1 }
			}

			fun restOf(list<i32> xs) -> list<i32> {
				match xs { [a, ..rest] => rest; _ => xs }
			}
			""";

		var list = List(1, 2, 3);
		AssertI32(1, RunSource(source, "head", list), "[a, ..rest] связывает a = 1");

		var rest = RunSource(source, "restOf", list);
		var items = Assert.IsType<KList>(rest.Ref);
		Assert.Equal(2, items.Count);
		Assert.Equal(KValue.I32(2), items[0]);
		Assert.Equal(KValue.I32(3), items[1]);

		// Связанный rest имеет статический тип list<T> (а не array<T> и не T).
		var match = SingleMatch(Bind(source), "restOf");
		var pattern = Assert.IsType<BoundListPattern>(match.Arms[0].Pattern);
		Assert.True(pattern.HasRest);
		Assert.Equal(1, pattern.ItemsBeforeRest);
		Assert.NotNull(pattern.Rest);
		Assert.Equal("list<i32>", pattern.Rest!.Type.ToString());
		Assert.True(pattern.Rest.Slot >= 0);
	}

	[Fact]
	public void Middle_rest_binds_items_around_it()
	{
		const string source =
			"""
			fun ends(list<i32> xs) -> i32 {
				match xs { [a, .., z] => a * 10 + z; _ => -1 }
			}

			fun middleOf(list<i32> xs) -> list<i32> {
				match xs { [a, ..rest, z] => rest; _ => xs }
			}
			""";

		AssertI32(13, RunSource(source, "ends", List(1, 2, 3)), "[a, .., z] связывает 1 и 3");
		AssertI32(13, RunSource(source, "ends", List(1, 3)), "двухэлементный список совпадает с [a, .., z]");

		var middle = Assert.IsType<KList>(RunSource(source, "middleOf", List(1, 2, 3)).Ref);
		Assert.Equal(1, middle.Count);
		Assert.Equal(KValue.I32(2), middle[0]);

		var empty = Assert.IsType<KList>(RunSource(source, "middleOf", List(1, 3)).Ref);
		Assert.Equal(0, empty.Count);
	}

	[Fact]
	public void Structural_rest_accepts_remaining_fields_and_reports_bad_forms()
	{
		const string source =
			"""
			unit Point(i32 x, i32 y)

			fun positional() -> i32 {
				Point p = Point(3, 4)
				match p { Point(x, ..) => x; _ => -1 }
			}

			fun named() -> i32 {
				Point p = Point(3, 4)
				match p { Point { x, .. } => x; _ => -1 }
			}
			""";

		AssertI32(3, RunSource(source, "positional"), "Point(x, ..) принимает лишнее поле y");
		AssertI32(3, RunSource(source, "named"), "Point { x, .. } принимает лишнее поле y");

		var namedRest = Diagnostics(
			"""
			unit Point(i32 x, i32 y)

			fun f(Point p) -> i32 {
				match p { Point { x, ..rest } => x; _ => -1 }
			}
			""");
		Assert.Contains(namedRest, diagnostic => diagnostic.Code == "KRN2114" && diagnostic.IsError);

		var twoRests = Diagnostics(
			"""
			unit Point(i32 x, i32 y)

			fun f(Point p) -> i32 {
				match p { Point { x, .., .. } => x; _ => -1 }
			}
			""");
		Assert.Contains(twoRests, diagnostic => diagnostic.Code == "KRN2113" && diagnostic.IsError);
	}

	[Fact]
	public void List_pattern_diagnostics_use_stable_codes()
	{
		var nonList = Diagnostics(
			"""
			fun f(i32 x) {
				match x { [a] => 1; _ => 0 }
			}
			""");
		Assert.Contains(nonList, diagnostic => diagnostic.Code == "KRN2112" && diagnostic.IsError);

		var twoRests = Diagnostics(
			"""
			fun f(list<i32> xs) {
				match xs { [.., ..rest] => 1; _ => 0 }
			}
			""");
		Assert.Contains(twoRests, diagnostic => diagnostic.Code == "KRN2113" && diagnostic.IsError);
	}

	/// <summary>Список <c>list&lt;i32&gt;</c> из значений для аргумента функции.</summary>
	private static KValue List(params int[] values) =>
		KValue.Obj(new KList(null, [.. values.Select(value => KValue.I32(value))]));

	/// <summary>Binder без codegen — для проверки формы связанного паттерна.</summary>
	private static BoundModule Bind(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		var bound = new Binder(module, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);
		return bound;
	}

	private static BoundMatchExpression SingleMatch(BoundModule module, string functionName)
	{
		var function = module.FindFunction(functionName)!;
		var statement = Assert.IsType<BoundExpressionStatement>(Assert.Single(function.Body.Statements));
		return Assert.IsType<BoundMatchExpression>(statement.Expression);
	}

	private static void AssertI32(int expected, KValue actual, string message) =>
		Assert.True(
			actual.Equals(KValue.I32(expected)),
			$"{message}: ожидалось {expected}, получено {actual}");

	/// <summary>Парсер → объявления → Binder → CodeGenerator → Interpreter; ошибки компиляции — падение.</summary>
	private static KValue RunSource(string source, string function, params KValue[] arguments)
	{
		var parsed = Parser.Parse(source);
		Assert.True(parsed.Diagnostics.Count == 0, "Парсер вернул ошибки: " + Describe(parsed.Diagnostics));
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.True(tableDiagnostics.Count == 0, "Сбор символов вернул ошибки: " + Describe(tableDiagnostics));
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.True(collectorDiagnostics.Count == 0, "Сбор объявлений вернул ошибки: " + Describe(collectorDiagnostics));
		var bound = new Binder(module, table).Bind();
		Assert.True(
			bound.Diagnostics.All(diagnostic => !diagnostic.IsError),
			"Binder вернул ошибки: " + Describe(bound.Diagnostics));
		var result = new CodeGenerator().Generate(bound);
		Assert.True(
			result.Diagnostics.All(diagnostic => !diagnostic.IsError),
			"CodeGenerator вернул ошибки: " + Describe(result.Diagnostics));
		return new Interpreter(result.Module).Run(function, arguments);
	}

	private static IReadOnlyList<Diagnostic> Diagnostics(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		var bound = new Binder(module, table).Bind();
		return bound.Diagnostics;
	}

	private static string Describe(IEnumerable<Diagnostic> diagnostics) => string.Join("; ", diagnostics);
}
