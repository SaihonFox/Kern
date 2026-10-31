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

namespace Kern.Tests.Semantics.Binding;

/// <summary>
/// B2: предупреждение KRN2305 об отброшенном значении <c>result&lt;T, E&gt;</c>/<c>task&lt;T&gt;</c>
/// и явный отказ <c>_ = expr</c>. <c>_</c> не переменная: читать её нельзя, а из операторов
/// допустим только <c>=</c>.
/// </summary>
public sealed class DiscardWarningTests
{
	[Fact]
	public void Result_value_as_a_statement_warns_KRN2305()
	{
		var diagnostics = Diagnostics("""
			fun g() -> result<i32, str> { panic("x") }
			fun main { g() }
			""");

		var warning = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2305");
		Assert.False(warning.IsError);
		Assert.Contains("result<i32,str>", warning.Message);
	}

	[Fact]
	public void Task_value_as_a_statement_warns_KRN2305()
	{
		var diagnostics = Diagnostics("""
			fun t() -> task<i32> { panic("x") }
			fun main { t() }
			""");

		var warning = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2305");
		Assert.False(warning.IsError);
		Assert.Contains("task<i32>", warning.Message);
	}

	[Fact]
	public void Discard_of_result_call_does_not_warn_and_lowers_to_discard_statement()
	{
		var module = Bind("""
			fun g() -> result<i32, str> { panic("x") }
			fun main { _ = g() }
			""");

		var discard = Assert.IsType<BoundDiscardStatement>(
			module.FindFunction("main")!.Body.Statements[0]);
		Assert.IsType<BoundCallExpression>(discard.Value);
	}

	[Fact]
	public void Discard_of_task_call_does_not_warn()
	{
		var module = Bind("""
			fun t() -> task<i32> { panic("x") }
			fun main { _ = t() }
			""");

		var discard = Assert.IsType<BoundDiscardStatement>(
			module.FindFunction("main")!.Body.Statements[0]);
		Assert.IsType<BoundCallExpression>(discard.Value);
	}

	[Fact]
	public void Result_assigned_to_a_local_does_not_warn()
	{
		var diagnostics = Diagnostics("""
			fun g() -> result<i32, str> { panic("x") }
			fun main { result<i32, str> r = g() }
			""");

		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "KRN2305");
		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.IsError);
	}

	[Fact]
	public void Result_as_a_function_body_tail_is_consumed_without_warnings()
	{
		var module = Bind("""
			fun g() -> result<i32, str> { panic("x") }
			fun f() -> result<i32, str> { g() }
			""");

		var function = Assert.IsType<BoundFunction>(module.FindFunction("f"));
		Assert.NotNull(function.Body.Value);
	}

	[Fact]
	public void Result_returned_with_ret_is_consumed_without_warnings()
	{
		Bind("""
			fun g() -> result<i32, str> { panic("x") }
			fun f() -> result<i32, str> { ret g() }
			""");
	}

	[Fact]
	public void Void_call_as_a_statement_does_not_warn()
	{
		var diagnostics = Diagnostics("""
			fun main { println("ок") }
			""");

		Assert.Empty(diagnostics);
	}

	[Fact]
	public void Compound_assignment_to_discard_target_reports_KRN2106()
	{
		var diagnostics = Diagnostics("""
			fun main { _ += 1 }
			""");

		var diagnostic = Assert.Single(diagnostics);
		Assert.Equal("KRN2106", diagnostic.Code);
		Assert.True(diagnostic.IsError);
	}

	[Fact]
	public void Discard_target_is_not_readable()
	{
		var diagnostics = Diagnostics("""
			fun main {
				i32 x = _
			}
			""");

		var error = Assert.Single(diagnostics, diagnostic => diagnostic.IsError);
		Assert.Equal("KRN2103", error.Code);
		Assert.Contains("'_'", error.Message);
	}

	[Fact]
	public void If_without_else_warns_exactly_once_about_the_discarded_then_value()
	{
		var diagnostics = Diagnostics("""
			fun g() -> result<i32, str> { panic("x") }
			fun f(i32 cond) {
				if cond > 0 { g() }
			}
			""");

		var warning = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2305");
		Assert.False(warning.IsError);
	}

	[Fact]
	public void Discard_of_a_plain_value_is_accepted()
	{
		var diagnostics = Diagnostics("""
			fun main { _ = 1 }
			""");

		Assert.Empty(diagnostics);
	}

	[Fact]
	public void Loop_body_warns_only_about_the_non_tail_discarded_value()
	{
		var diagnostics = Diagnostics("""
			fun g() -> result<i32, str> { panic("x") }
			fun f {
				loop {
					g()
					g()
				}
			}
			""");

		Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2305");
	}

	[Fact]
	public void Discard_statement_evaluates_the_call_for_its_side_effect()
	{
		// result<T, E> пока не имеет ни конструкторов (Ok/Err), ни представления в VM,
		// поэтому счётчик возвращает i32. Путь codegen для `_ =` один и тот же:
		// вызов остаётся на стеке, затем Pop; типизацию result/task проверяют тесты выше.
		var value = Run("""
			type Counter {
				#i32 total
				fun take() -> i32 {
					total = total + 1
					total
				}
			}

			fun f() -> i32 {
				Counter.total = 0
				Counter b = Counter()
				_ = b.take()
				_ = b.take()
				Counter.total
			}
			""", "f");

		Assert.Equal(KValue.I32(2), value);
	}

	private static BoundModule Bind(string source)
	{
		var (module, diagnostics) = BindWithDiagnostics(source);
		Assert.Empty(diagnostics);
		return module;
	}

	private static IReadOnlyList<Diagnostic> Diagnostics(string source) =>
		BindWithDiagnostics(source).Diagnostics;

	private static (BoundModule Module, IReadOnlyList<Diagnostic> Diagnostics) BindWithDiagnostics(string source)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		var bound = new Binder(module, table).Bind();
		return (bound, bound.Diagnostics);
	}

	private static KValue Run(string source, string function)
	{
		var parsed = Parser.Parse(source);
		Assert.Empty(parsed.Diagnostics);
		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		Assert.Empty(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		Assert.Empty(collectorDiagnostics);
		var bound = new Binder(module, table).Bind();
		Assert.DoesNotContain(bound.Diagnostics, diagnostic => diagnostic.IsError);
		var result = new CodeGenerator().Generate(bound);
		Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError);
		return new Interpreter(result.Module).Run(function, []);
	}
}
