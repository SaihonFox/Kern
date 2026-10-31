using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.CodeGen;

/// <summary>
/// M3-D3b: статические поля и статические автосвойства (spec §8, «Статика»).
/// Статический слот живёт в модульном <c>KbcModule.FieldPool</c>, а не в экземпляре,
/// поэтому все объекты типа видят одно значение. Читать/писать можно через <c>T.field</c>
/// из любого места или без квалификации внутри члена своего типа (локальные имена важнее).
/// Инициализаторы статики выполняет модульный <c>&lt;static-init&gt;</c> (Q15b); в тестах
/// слоты всё равно засеваются явно, чтобы ожидаемое значение было видно рядом с проверкой.
/// </summary>
public sealed class StaticFieldTests
{
	[Fact]
	public void Static_field_is_shared_by_all_instances()
	{
		// Один модульный слот: два разных объекта увеличивают одно и то же значение.
		var result = Run(
			"""
			type Counter {
				#i32 total
				fun bump() { total = total + 1 }
			}

			fun f() -> i32 {
				Counter.total = 0
				Counter a = Counter()
				Counter b = Counter()
				a.bump()
				b.bump()
				Counter.total
			}
			""",
			"f");

		Assert.Equal(KValue.I32(2), result);
	}

	[Fact]
	public void Type_qualified_write_is_visible_to_main()
	{
		// Запись через имя типа идёт в модульный слот, а не в какой-то экземпляр.
		Assert.Equal(
			KValue.I32(6),
			Run(
				"""
				type Counter {
					#i32 total
				}

				fun f() -> i32 {
					Counter.total = 5
					Counter.total + 1
				}
				""",
				"f"));
	}

	[Fact]
	public void Static_fields_of_one_type_use_distinct_slots()
	{
		// У каждого статического поля свой слот в модульном FieldPool: запись в visits
		// не перезаписывает total. Если бы слот был один, получилось бы 77.
		Assert.Equal(
			KValue.I32(37),
			Run(
				"""
				type Counter {
					#i32 total
					#i32 visits
				}

				fun f() -> i32 {
					Counter.total = 3
					Counter.visits = 7
					Counter.total * 10 + Counter.visits
				}
				""",
				"f"));
	}

	[Fact]
	public void Instance_method_sees_the_type_qualified_write()
	{
		// Экземплярный метод читает тот же статический слот, что заполнил main.
		Assert.Equal(
			KValue.I32(7),
			Run(
				"""
				type Counter {
					#i32 total
					fun current() -> i32 { total }
				}

				fun f() -> i32 {
					Counter.total = 7
					Counter c = Counter()
					c.current()
				}
				""",
				"f"));
	}

	[Fact]
	public void Static_method_reads_the_static_field()
	{
		// Статический метод без `$` видит статическое поле своего типа без квалификации.
		// Вызов `Counter.read()` пока не привязывается, поэтому метод запускается как
		// самостоятельная функция на том же интерпретаторе после main.
		var result = Compile(
			"""
			type Counter {
				#i32 total
				#fun read() -> i32 { total }
			}

			fun main {
				Counter.total = 33
			}
			""");

		var interpreter = new Interpreter(result.Module);
		interpreter.Run("main");
		Assert.Equal(KValue.I32(33), interpreter.Run("read"));
	}

	[Fact]
	public void Local_named_like_static_field_shadows_it()
	{
		// Локальная `total` перекрывает статическое поле: запись в неё не трогает слот.
		// 701 = 7 (локальная после += 2) * 100 + 1 (статика осталась прежней).
		Assert.Equal(
			KValue.I32(701),
			Run(
				"""
				type Counter {
					#i32 total
					fun local_value() -> i32 {
						i32 total = 5
						total = total + 2
						total
					}
				}

				fun f() -> i32 {
					Counter.total = 1
					Counter c = Counter()
					c.local_value() * 100 + Counter.total
				}
				""",
				"f"));
	}

	[Fact]
	public void Parameter_named_like_static_field_shadows_it()
	{
		// Параметр перекрывает статическое поле так же, как локальная переменная.
		Assert.Equal(
			KValue.I32(902),
			Run(
				"""
				type Counter {
					#i32 total
					fun parameter_value(i32 total) -> i32 { total }
				}

				fun f() -> i32 {
					Counter.total = 2
					Counter c = Counter()
					c.parameter_value(9) * 100 + Counter.total
				}
				""",
				"f"));
	}

	[Fact]
	public void Write_to_required_static_field_reports_KRN2105()
	{
		// `!` у статики — константа: обычный код её не меняет, запись разрешена только
		// статическому инициализатору своего типа (Q15b).
		var diagnostics = Diagnostics(
			"""
			type Counter {
				#i32! limit
			}

			fun main {
				Counter.limit = 1
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN2105", diagnostic.Code);
		Assert.Contains("Статическая константа", diagnostic.Message);
	}

	[Fact]
	public void Reading_static_field_through_instance_reports_KRN2103()
	{
		var diagnostics = Diagnostics(
			"""
			type Counter {
				#i32 total
				i32 own
			}

			fun main {
				Counter c = Counter()
				i32 x = c.total
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN2103", diagnostic.Code);
		Assert.Contains("через экземпляр", diagnostic.Message);
	}

	[Fact]
	public void Reading_static_field_through_self_reports_KRN2103()
	{
		var diagnostics = Diagnostics(
			"""
			type Counter {
				#i32 total
				fun peek() -> i32 { $total }
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN2103", diagnostic.Code);
		Assert.Contains("Статический член", diagnostic.Message);
	}

	[Fact]
	public void Reading_instance_field_through_type_reports_KRN2103()
	{
		// Обратное направление: экземплярный член нельзя прочитать через имя типа.
		var diagnostics = Diagnostics(
			"""
			type Counter {
				#i32 total
				i32 own
			}

			fun f() -> i32 {
				Counter.own
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN2103", diagnostic.Code);
		Assert.Contains("не статический", diagnostic.Message);
	}

	[Fact]
	public void Static_unit_field_stores_an_object_and_reads_its_field()
	{
		// Статическое поле может хранить unit; чтение `Holder.origin.x` идёт через
		// ссылку из статического слота.
		Assert.Equal(
			KValue.I32(3),
			Run(
				"""
				unit Point(i32 x)

				type Holder {
					#Point origin
				}

				fun f() -> i32 {
					Holder.origin = Point(3)
					Holder.origin.x
				}
				""",
				"f"));
	}

	[Fact]
	public void Static_auto_property_round_trips_through_its_backing_slot()
	{
		// `#i32 visits { get; set }` — статическое автосвойство: запись и чтение идут
		// в статический backing-слот модуля.
		Assert.Equal(
			KValue.I32(4),
			Run(
				"""
				type Counter {
					#i32 visits { get; set }
				}

				fun f() -> i32 {
					Counter.visits = 4
					Counter.visits
				}
				""",
				"f"));
	}

	private static KValue Run(string source, string function, params KValue[] arguments)
	{
		var result = Compile(source);
		Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError);
		return new Interpreter(result.Module).Run(function, arguments);
	}

	private static CodegenResult Compile(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		var bound = new Binder(module, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);
		return new CodeGenerator().Generate(bound);
	}

	private static IReadOnlyList<Diagnostic> Diagnostics(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		return new Binder(module, table).Bind().Diagnostics;
	}
}
