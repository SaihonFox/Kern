using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

/// <summary>
/// M3-D4 / Q13 (spec §9, строка 500): запись через цепочку членов в значение
/// <c>unit</c> копирует промежуточные значения и записывает копию обратно во
/// владельца. Проверяется сквозной путь исходник → байткод → VM: локальный
/// корень, вложенные цепочки, статическое поле, звено-класс (без копии),
/// составное присваивание, вычисляемое свойство и корень <c>$</c>.
/// </summary>
public sealed class NestedUnitStoreTests
{
	[Fact]
	public void Local_unit_root_receives_the_write_back()
	{
		// o.inner.x = 5 меняет копию inner и возвращает её в локальную o:
		// без обратной записи чтение вернуло бы 1.
		const string source =
			"""
			unit Inner(i32 x)
			unit Outer(Inner inner)

			fun f() -> i32 {
				Outer o = Outer(Inner(1))
				o.inner.x = 5
				o.inner.x
			}
			""";

		AssertI32(5, RunSource(source, "f"), "локальный корень unit не получил обратную запись");
	}

	[Fact]
	public void Three_level_unit_chain_reads_the_stored_value()
	{
		// Два промежуточных unit-звена: каждая копия пишется обратно в своего
		// владельца, поэтому o.mid.inner.v возвращает записанное 9.
		const string source =
			"""
			unit Inner(i32 v)
			unit Mid(Inner inner)
			unit Outer(Mid mid)

			fun f() -> i32 {
				Outer o = Outer(Mid(Inner(1)))
				o.mid.inner.v = 9
				o.mid.inner.v
			}
			""";

		AssertI32(9, RunSource(source, "f"), "трёхуровневая цепочка unit не сохранила значение");
	}

	[Fact]
	public void Earlier_copy_stays_unchanged_after_a_nested_store()
	{
		// i = o.inner — независимая копия (spec §9). Последующая запись через
		// o.inner.x не должна её изменить: 1 * 10 + 5 = 15.
		const string source =
			"""
			unit Inner(i32 x)
			unit Outer(Inner inner)

			fun f() -> i32 {
				Outer o = Outer(Inner(1))
				Inner i = o.inner
				o.inner.x = 5
				i.x * 10 + o.inner.x
			}
			""";

		AssertI32(15, RunSource(source, "f"), "запись через цепочку изменила более раннюю копию unit");
	}

	[Fact]
	public void Static_unit_root_copy_back_keeps_the_snapshot()
	{
		// Q15a2: изменяемое статическое поле unit-типа; запись Holder.cache.x = 7
		// идёт через копию с обратной записью (Q13), а не теряется. Снимок snap,
		// снятый до записи, остаётся равен 1: 1 * 10 + 7 = 17.
		const string source =
			"""
			unit Inner(i32 x)
			type Holder {
				#Inner cache
			}

			fun f() -> i32 {
				Holder.cache = Inner(1)
				Inner snap = Holder.cache
				Holder.cache.x = 7
				snap.x * 10 + Holder.cache.x
			}
			""";

		AssertI32(17, RunSource(source, "f"), "статический корень unit не получил обратную запись или изменил снимок");
	}

	[Fact]
	public void Class_link_mutates_in_place_and_receiver_runs_once()
	{
		// Звенья-классы (`type`) меняются на месте, без копии, а приёмник
		// вычисляется ровно один раз: если get() вызвать дважды, calls стало бы 2
		// и результат был бы 205 вместо 105.
		const string source =
			"""
			type Node {
				i32 x
			}

			type Holder {
				Node node = Node()
				#i32 calls = 0
				#Holder shared = Holder()
			}

			fun get() -> Holder {
				Holder.calls = Holder.calls + 1
				Holder.shared
			}

			fun f() -> i32 {
				get().node.x = 5
				Holder.calls * 100 + Holder.shared.node.x
			}
			""";

		AssertI32(105, RunSource(source, "f"), "приёмник-класс вычислен не один раз или звено-класс не изменилось на месте");
	}

	[Fact]
	public void Compound_assignment_through_a_unit_chain_reads_the_current_value()
	{
		// o.inner.x += 2 читает текущее значение уже внутри цепи записи и
		// пишет результат обратно: 1 + 2 = 3.
		const string source =
			"""
			unit Inner(i32 x)
			unit Outer(Inner inner)

			fun f() -> i32 {
				Outer o = Outer(Inner(1))
				o.inner.x += 2
				o.inner.x
			}
			""";

		AssertI32(3, RunSource(source, "f"), "составное присваивание через цепочку unit дало неверный результат");
	}

	[Fact]
	public void Computed_property_link_writes_back_through_the_setter()
	{
		// Свойство inner — вычисляемое (getter + setter), звено unit: цепь
		// обязана вернуть изменённую копию в setter. Setter пишет v.x в stored,
		// поэтому 5 * 10 + 5 = 55; без вызова setter было бы 1 * 10 + 1 = 11.
		const string source =
			"""
			unit Inner(i32 x)
			unit Box(i32 stored) {
				Inner inner {
					get Inner($.stored)
					set(v) {
						$.stored = v.x
					}
				}
			}

			fun f() -> i32 {
				Box b = Box(1)
				b.inner.x = 5
				b.stored * 10 + b.inner.x
			}
			""";

		AssertI32(55, RunSource(source, "f"), "вычисляемое свойство не получило изменённую копию через setter");
	}

	[Fact]
	public void Self_root_inside_a_unit_method_reads_back()
	{
		// Внутри метода unit `$` — значение-приёмник (копия), поэтому проверяем
		// только чтение после записи внутри метода: запись через $ не обязана
		// менять объект вызывающего кода.
		const string source =
			"""
			unit Inner(i32 x)
			unit Outer(Inner inner) {
				fun bump() -> i32 {
					$.inner.x = 5
					$.inner.x
				}
			}

			fun f() -> i32 {
				Outer o = Outer(Inner(1))
				o.bump()
			}
			""";

		AssertI32(5, RunSource(source, "f"), "запись через корень $ внутри метода unit не читается обратно");
	}

	/// <summary>Сравнение целого результата с русским сообщением об ошибке.</summary>
	private static void AssertI32(int expected, KValue actual, string message) =>
		Assert.True(
			actual.Equals(KValue.I32(expected)),
			$"{message}: ожидалось {expected}, получено {actual}");

	/// <summary>
	/// Сквозной путь примера: парсер → сбор объявлений → Binder → CodeGenerator →
	/// Interpreter. Любая ошибка компиляции — падение теста с русским текстом.
	/// </summary>
	private static KValue RunSource(string source, string function)
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

	private static string Describe(IEnumerable<Kern.Core.Diagnostics.Diagnostic> diagnostics) =>
		string.Join("; ", diagnostics);
}
