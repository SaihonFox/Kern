using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

/// <summary>
/// B5a: пользовательский индексатор <c>op [](…)</c> и чтение <c>x[a, b]</c> (spec §15).
/// Проверяется сквозной путь исходник → Binder → CodeGenerator → VM: один и два
/// аргумента, результат в арифметике, приёмник-класс и диагностика KRN2101/KRN2102.
/// </summary>
public sealed class IndexerTests
{
	[Fact]
	public void Unit_indexer_returns_field_by_index()
	{
		// Индексатор unit'а читает `$`-поля через match: p[0] -> 3, p[1] -> 4.
		const string source =
			"""
			unit Pair(i32 x, i32 y) {
				op [](i32 i) -> i32 { match i { 0 => $x; _ => $y } }
			}

			fun first() -> i32 {
				Pair p = Pair(3, 4)
				p[0]
			}

			fun second() -> i32 {
				Pair p = Pair(3, 4)
				p[1]
			}
			""";

		Assert.Equal(KValue.I32(3), Run(source, "first"));
		Assert.Equal(KValue.I32(4), Run(source, "second"));
	}

	[Fact]
	public void Two_argument_indexer_uses_both_parameters()
	{
		// Арность индексатора должна совпасть с числом аргументов: g[1, 2] -> 3*10+1*2+2.
		const string source =
			"""
			unit Grid(i32 width) {
				op [](i32 row, i32 column) -> i32 { $width * 10 + row * 2 + column }
			}

			fun cell() -> i32 {
				Grid g = Grid(3)
				g[1, 2]
			}
			""";

		Assert.Equal(KValue.I32(34), Run(source, "cell"));
	}

	[Fact]
	public void Indexer_result_participates_in_arithmetic()
	{
		// Результат индексатора — обычное выражение типа R: 3 + 4 = 7.
		const string source =
			"""
			unit Pair(i32 x, i32 y) {
				op [](i32 i) -> i32 { match i { 0 => $x; _ => $y } }
			}

			fun total() -> i32 {
				Pair p = Pair(3, 4)
				p[0] + p[1]
			}
			""";

		Assert.Equal(KValue.I32(7), Run(source, "total"));
	}

	[Fact]
	public void Binder_reports_KRN2101_and_KRN2102_for_invalid_indexing()
	{
		// Нет `op []` у типа.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics(
					"""
					unit Point(i32 x)

					fun main {
						Point p = Point(1)
						p[0]
					}
					"""),
				item => item.Code == "KRN2101").Code);

		// Индексатор есть, но арность другая.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics(
					"""
					unit Point(i32 x) {
						op [](i32 i) -> i32 { $x + i }
					}

					fun main {
						Point p = Point(1)
						p[0, 1]
					}
					"""),
				item => item.Code == "KRN2101").Code);

		// Аргумент не преобразуется к типу параметра — KRN2102 из Convert.
		Assert.Equal(
			"KRN2102",
			Assert.Single(
				Diagnostics(
					"""
					unit Point(i32 x) {
						op [](i32 i) -> i32 { $x + i }
					}

					fun main {
						Point p = Point(1)
						p["a"]
					}
					"""),
				item => item.Code == "KRN2102").Code);

		// Индексатор без `-> R` не может дать значение.
		Assert.Equal(
			"KRN2101",
			Assert.Single(
				Diagnostics(
					"""
					unit Point(i32 x) {
						op [](i32 i) { $x + i }
					}

					fun main {
						Point p = Point(1)
						p[0]
					}
					"""),
				item => item.Code == "KRN2101").Code);

		// Встроенные коллекции не ищут пользовательский `op []`: list/array/dict получили
		// встроенное правило в B5b1, а `str` — в B5b2. Лишний индекс у str — KRN2101.
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

		// Запись `p[0] = 5` остаётся недопустимой целью: setter'ы не поддержаны (read-only).
		Assert.Contains(
			Diagnostics(
				"""
				unit Point(i32 x) {
					op [](i32 i) -> i32 { $x + i }
				}

				fun main {
					Point p = Point(1)
					p[0] = 5
				}
				"""),
			item => item.IsError);
	}

	[Fact]
	public void Class_receiver_indexer_binds_and_runs()
	{
		// Приёмник-класс идёт тем же путём: `$` — ссылка на экземпляр, c[3] -> 5*3.
		const string source =
			"""
			type Cell(i32 value) {
				op [](i32 i) -> i32 { $value * i }
			}

			fun scaled() -> i32 {
				Cell c = Cell(5)
				c[3]
			}
			""";

		Assert.Equal(KValue.I32(15), Run(source, "scaled"));
	}

	/// <summary>
	/// Сквозной путь примера: парсер → сбор объявлений → Binder → CodeGenerator → Interpreter.
	/// Любая ошибка компиляции — падение теста с русским текстом.
	/// </summary>
	private static KValue Run(string source, string function)
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
		return new Interpreter(result.Module).Run(function, []);
	}

	/// <summary>Диагностики Binder'а без сквозного запуска.</summary>
	private static IReadOnlyList<Diagnostic> Diagnostics(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.True(parsed.Diagnostics.Count == 0, "Парсер вернул ошибки: " + Describe(parsed.Diagnostics));
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.True(tableDiagnostics.Count == 0, "Сбор символов вернул ошибки: " + Describe(tableDiagnostics));
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.True(collectorDiagnostics.Count == 0, "Сбор объявлений вернул ошибки: " + Describe(collectorDiagnostics));
		return new Binder(module, table).Bind().Diagnostics;
	}

	private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
		string.Join("; ", diagnostics);
}
