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
/// M3-D3b, часть 2b: статические инициализаторы (Q15b, spec §8). Они выполняются один раз
/// до точки входа, в порядке объявления: поля и автосвойства одного типа — в его
/// <c>&lt;static-init&gt;</c>, сами типы — в модульном <c>&lt;static-init&gt;</c> по позиции
/// объявления. <c>init</c>-записи там запрещены (KRN2105), а запись <c>!</c>-статики
/// разрешена только её собственному инициализатору.
/// </summary>
public sealed class StaticInitializerTests
{
	[Fact]
	public void Static_field_initializer_runs_before_the_entry_function()
	{
		// Слота никто не засевал: 5 приходит только из статического инициализатора.
		Assert.Equal(
			KValue.I32(5),
			Run(
				"""
				type Counter {
					#i32 total = 5
				}

				fun f() -> i32 { Counter.total }
				""",
				"f"));
	}

	[Fact]
	public void Static_initializers_run_in_declaration_order()
	{
		// b читает a после того, как пролог уже записал 2 в слот a.
		Assert.Equal(
			KValue.I32(5),
			Run(
				"""
				type Counter {
					#i32 a = 2
					#i32 b = a + 3
				}

				fun f() -> i32 { Counter.b }
				""",
				"f"));
	}

	[Fact]
	public void Static_auto_property_initializer_writes_its_backing_slot()
	{
		Assert.Equal(
			KValue.I32(4),
			Run(
				"""
				type Counter {
					#i32 visits { get; set } = 4
				}

				fun f() -> i32 { Counter.visits }
				""",
				"f"));
	}

	[Fact]
	public void Static_initializer_calls_a_module_function()
	{
		Assert.Equal(
			KValue.I32(8),
			Run(
				"""
				fun seed() -> i32 { 7 }

				type Counter {
					#i32 total = seed() + 1
				}

				fun f() -> i32 { Counter.total }
				""",
				"f"));
	}

	[Fact]
	public void Static_initializer_calls_a_constructor()
	{
		Assert.Equal(
			KValue.I32(3),
			Run(
				"""
				unit Point(i32 x, i32 y)

				type Holder {
					#Point origin = Point(3, 4)
				}

				fun f() -> i32 { Holder.origin.x }
				""",
				"f"));
	}

	[Fact]
	public void Later_type_initializer_reads_an_earlier_type_static()
	{
		// Порядок модульного <static-init> — объявление типов в файле: Second видит
		// уже инициализированное First.base.
		Assert.Equal(
			KValue.I32(15),
			Run(
				"""
				type First {
					#i32 base = 10
				}

				type Second {
					#i32 derived = First.base + 5
				}

				fun f() -> i32 { Second.derived }
				""",
				"f"));
	}

	[Fact]
	public void Required_static_constant_keeps_its_initializer()
	{
		// `!` у статики — константа, но её значение задаёт тот же статический инициализатор.
		Assert.Equal(
			KValue.I32(5),
			Run(
				"""
				type Counter {
					#i32! limit = 5
				}

				fun f() -> i32 { Counter.limit }
				""",
				"f"));
	}

	[Fact]
	public void Required_static_is_writable_inside_its_own_static_initializer()
	{
		// Единственное место, где запись `!`-статики разрешена (Q15b): её инициализатор.
		Assert.Equal(
			KValue.I32(909),
			Run(
				"""
				type Counter {
					#i32! limit = 5
					#i32 bumped = match 1 {
						1 => { Counter.limit = 9; Counter.limit }
						_ => 0
					}
				}

				fun f() -> i32 { Counter.bumped * 100 + Counter.limit }
				""",
				"f"));
	}

	[Fact]
	public void Write_to_required_static_in_ordinary_code_reports_KRN2105()
	{
		var diagnostics = Diagnostics(
			"""
			type Counter {
				#i32! limit = 5
			}

			fun f() {
				Counter.limit = 1
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN2105", diagnostic.Code);
		Assert.Contains("Статическая константа", diagnostic.Message);
	}

	[Fact]
	public void Init_write_inside_a_static_initializer_reports_KRN2105()
	{
		// Статического «создания объекта» нет: init-запись запрещена и здесь (Q15b).
		var diagnostics = Diagnostics(
			"""
			type Counter {
				#i32 p { get; init }
				#i32 q = match 1 {
					1 => { Counter.p = 1; 0 }
					_ => 0
				}
			}
			""");

		Assert.Contains(
			diagnostics,
			diagnostic => diagnostic.Code == "KRN2105" && diagnostic.Message.Contains("'init'"));
	}

	[Fact]
	public void Static_init_runs_once_per_interpreter()
	{
		var result = Compile(
			"""
			fun bump() -> i32 {
				Counter.hits = Counter.hits + 1
				Counter.hits
			}

			type Counter {
				#i32 hits = 0
				#i32 marker = bump()
			}

			fun f() -> i32 { Counter.hits }
			""");

		var interpreter = new Interpreter(result.Module);

		Assert.Equal(KValue.I32(1), interpreter.Run("f"));
		Assert.Equal(KValue.I32(1), interpreter.Run("f"));
	}

	[Fact]
	public void Module_static_init_is_wired_with_a_unique_name_per_type()
	{
		var result = Compile(
			"""
			type Counter {
				#i32 total = 5
			}

			fun f() -> i32 { Counter.total }
			""");

		Assert.True(result.Module.StaticInit >= 0);
		var moduleInitializer = result.Module.FunctionPool[result.Module.StaticInit];
		Assert.Equal("<static-init>", moduleInitializer.Name);
		Assert.Contains(
			result.Module.FunctionPool,
			function => function.Name == "Counter.<static-init>");
	}

	[Fact]
	public void Module_without_static_initializers_has_no_static_init()
	{
		var result = Compile(
			"""
			type Counter {
				#i32 total
			}

			fun f() -> i32 {
				Counter.total
			}
			""");

		Assert.Equal(-1, result.Module.StaticInit);
	}

	[Fact]
	public void Write_to_required_static_of_another_type_reports_KRN2105()
	{
		// `!`-константа пишется только инициализатором объявившего её типа (Q15b):
		// статический инициализатор B не может трогать A.LIMIT.
		var diagnostics = Diagnostics(
			"""
			type A {
				#i32! LIMIT = 1
			}

			type B {
				#i32 probe = match 1 {
					1 => { A.LIMIT = 2; 0 }
					_ => 0
				}
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN2105", diagnostic.Code);
		Assert.Contains("Статическая константа", diagnostic.Message);
	}

	[Fact]
	public void Self_expression_inside_a_static_initializer_reports_KRN2106()
	{
		// У статического инициализатора нет `$` (Q15b): приёмник-экземпляр не заводится.
		var diagnostics = Diagnostics(
			"""
			type T {
				#i32 v = $x
			}
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN2106", diagnostic.Code);
		Assert.Contains("'$'", diagnostic.Message);
	}

	[Fact]
	public void Dependent_type_declared_first_reads_none_and_panics()
	{
		// Порядок модульного <static-init> — объявление типов в файле, а не зависимости:
		// Second объявлен раньше First и читает ещё не записанный слот First.base.
		// До инициализации слот статики — KValue.None, поэтому `none + i32` паникует.
		var result = Compile(
			"""
			type Second {
				#i32 derived = First.base + 5
			}

			type First {
				#i32 base = 10
			}

			fun f() -> i32 { Second.derived }
			""");

		var panic = Assert.Throws<KernPanic>(() => new Interpreter(result.Module).Run("f"));
		Assert.Contains("несовпадение типов операндов", panic.Message);
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
