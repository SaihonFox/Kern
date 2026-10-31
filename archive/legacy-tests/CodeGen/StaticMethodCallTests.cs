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
/// M3-D3b: вызов статических методов (spec §8, «Статика»; <c>#fun</c> — это <c>static</c>).
/// Статический метод вызывается через имя типа (<c>T.method(...)</c>) или без квалификации
/// внутри члена своего типа; приёмник-экземпляр в байткод не попадает, поэтому аргументы
/// эмитятся ровно по параметрам. Обратные направления (<c>obj.staticMethod()</c>,
/// <c>$staticMethod()</c>, <c>T.instanceMethod()</c>) остаются <c>KRN2103</c>.
/// </summary>
public sealed class StaticMethodCallTests
{
	[Fact]
	public void Unqualified_static_call_inside_instance_method_returns_the_static_field()
	{
		// `read()` внутри метода того же типа видит статический метод без квалификации.
		Assert.Equal(
			KValue.I32(33),
			Run(
				"""
				type Counter {
					#i32 total
					fun read_via_self() -> i32 { read() }
					#fun read() -> i32 { total }
				}

				fun f() -> i32 {
					Counter.total = 33
					Counter c = Counter()
					c.read_via_self()
				}
				""",
				"f"));
	}

	[Fact]
	public void Type_qualified_static_call_returns_the_static_field()
	{
		// `Counter.read()` — приёмник это имя типа, а не экземпляр.
		Assert.Equal(
			KValue.I32(44),
			Run(
				"""
				type Counter {
					#i32 total
					#fun read() -> i32 { total }
				}

				fun f() -> i32 {
					Counter.total = 44
					Counter.read()
				}
				""",
				"f"));
	}

	[Fact]
	public void Type_qualified_static_call_passes_arguments_without_a_phantom_receiver()
	{
		// Если бы в вызов подставлялся приёмник, аргументов стало бы три и вызов не сошёлся.
		Assert.Equal(
			KValue.I32(5),
			Run(
				"""
				type Counter {
					#fun add(i32 a, i32 b) -> i32 { a + b }
				}

				fun f() -> i32 {
					Counter.add(2, 3)
				}
				""",
				"f"));
	}

	[Fact]
	public void Static_overloads_resolve_by_arity_from_both_call_forms()
	{
		// 11 (Counter.add(1) = 1 + 10) * 100 + 15 (add(2, 3) = 2 + 3 + 10) = 1115.
		Assert.Equal(
			KValue.I32(1115),
			Run(
				"""
				type Counter {
					#i32 total
					#fun add(i32 a) -> i32 { a + total }
					#fun add(i32 a, i32 b) -> i32 { a + b + total }
					fun qualified() -> i32 { Counter.add(1) }
					fun unqualified() -> i32 { add(2, 3) }
				}

				fun f() -> i32 {
					Counter.total = 10
					Counter c = Counter()
					c.qualified() * 100 + c.unqualified()
				}
				""",
				"f"));
	}

	[Fact]
	public void Instance_and_static_method_with_the_same_name_do_not_hijack_each_other()
	{
		// Одинаковая сигнатура у instance и static в одном типе запрещена (KRN1400),
		// поэтому методы различаются арностью: `c.m()` идёт в экземплярный, `Counter.m(2)`
		// — в статический. 1 * 100 + (2 + 10) = 112.
		Assert.Equal(
			KValue.I32(112),
			Run(
				"""
				type Counter {
					#i32 total
					fun m() -> i32 { 1 }
					#fun m(i32 a) -> i32 { a + total }
				}

				fun f() -> i32 {
					Counter.total = 10
					Counter c = Counter()
					c.m() * 100 + Counter.m(2)
				}
				""",
				"f"));
	}

	[Fact]
	public void Owner_statics_and_module_functions_share_one_overload_set()
	{
		// Кандидаты не сливаются вручную: `pick` модуля и статический `pick` типа вместе
		// участвуют в выборе перегрузки, поэтому подходит всегда более точная сигнатура.
		Assert.Equal(
			KValue.I32(7101),
			Run(
				"""
				fun pick(i32 x) -> i32 { x + 100 }

				type Chooser {
					#fun pick(str s) -> i32 { 7 }
					fun from_static() -> i32 { pick("a") }
					fun from_module() -> i32 { pick(1) }
				}

				fun f() -> i32 {
					Chooser c = Chooser()
					c.from_static() * 1000 + c.from_module()
				}
				""",
				"f"));
	}

	[Fact]
	public void Static_method_through_instance_reports_KRN2103()
	{
		var diagnostic = Assert.Single(Diagnostics(
			"""
			type Counter {
				#fun read() -> i32 { 1 }
			}

			fun main {
				Counter c = Counter()
				i32 x = c.read()
			}
			"""));

		Assert.Equal("KRN2103", diagnostic.Code);
		Assert.Contains("через экземпляр", diagnostic.Message);
	}

	[Fact]
	public void Static_method_through_self_reports_KRN2103()
	{
		var diagnostic = Assert.Single(Diagnostics(
			"""
			type Counter {
				#fun read() -> i32 { 1 }
				fun peek() -> i32 { $read() }
			}
			"""));

		Assert.Equal("KRN2103", diagnostic.Code);
		Assert.Contains("через экземпляр", diagnostic.Message);
	}

	[Fact]
	public void Instance_method_through_type_reports_KRN2103()
	{
		var diagnostic = Assert.Single(Diagnostics(
			"""
			type Counter {
				i32 own
				fun read() -> i32 { $own }
			}

			fun f() -> i32 { Counter.read() }
			"""));

		Assert.Equal("KRN2103", diagnostic.Code);
		Assert.Contains("не статический", diagnostic.Message);
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
