using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.CodeGen;

/// <summary>
/// M4-A: сквозные тесты codegen → .kbc-структура → VM. Каждая функция из исходника
/// компилируется и исполняется интерпретатором напрямую, без записи файла.
/// </summary>
public sealed class CodeGeneratorTests
{
	[Fact]
	public void Constant_expression_returns_value()
	{
		Assert.Equal(KValue.I32(3), Run("fun f() -> i32 { 1 + 2 }", "f"));
	}

	[Fact]
	public void Numeric_separators_are_ignored_in_values()
	{
		Assert.Equal(KValue.I32(1_000_000), Run("fun f() -> i32 { 1_000_000 }", "f"));
	}

	[Fact]
	public void Parameters_arrive_in_slots()
	{
		Assert.Equal(
			KValue.I32(5),
			Run("fun add(i32 a, i32 b) -> i32 { a + b }", "add", KValue.I32(2), KValue.I32(3)));
	}

	[Fact]
	public void Local_declaration_and_assignment()
	{
		var result = Run(
			"""
			fun f(i32 x) -> i32 {
				i32 y = x * 2
				y = y + 1
				y
			}
			""",
			"f",
			KValue.I32(5));

		Assert.Equal(KValue.I32(11), result);
	}

	[Fact]
	public void If_else_materializes_a_value()
	{
		const string source = "fun f(i32 x) -> i32 { if x > 0 { 10 } else { 20 } }";
		Assert.Equal(KValue.I32(10), Run(source, "f", KValue.I32(1)));
		Assert.Equal(KValue.I32(20), Run(source, "f", KValue.I32(-1)));
	}

	[Fact]
	public void If_without_else_updates_a_local()
	{
		const string source =
			"""
			fun f(i32 x) -> i32 {
				i32 y = 0
				if x > 0 { y = 1 }
				y
			}
			""";
		Assert.Equal(KValue.I32(1), Run(source, "f", KValue.I32(7)));
		Assert.Equal(KValue.I32(0), Run(source, "f", KValue.I32(-7)));
	}

	[Fact]
	public void Logical_and_short_circuits()
	{
		const string source = "fun f(i32 x) -> bool { x > 2 && x < 5 }";
		Assert.Equal(KValue.True, Run(source, "f", KValue.I32(3)));
		Assert.Equal(KValue.False, Run(source, "f", KValue.I32(1)));
		Assert.Equal(KValue.False, Run(source, "f", KValue.I32(9)));
	}

	[Fact]
	public void Logical_or_short_circuits()
	{
		const string source = "fun f(i32 x) -> bool { x < 0 || x > 10 }";
		Assert.Equal(KValue.True, Run(source, "f", KValue.I32(-1)));
		Assert.Equal(KValue.True, Run(source, "f", KValue.I32(11)));
		Assert.Equal(KValue.False, Run(source, "f", KValue.I32(5)));
	}

	[Fact]
	public void User_function_call()
	{
		const string source =
			"""
			fun inc(i32 x) -> i32 { x + 1 }
			fun f() -> i32 { inc(41) }
			""";
		Assert.Equal(KValue.I32(42), Run(source, "f"));
	}

	[Fact]
	public void Return_statement_stops_the_body()
	{
		const string source =
			"""
			fun f() -> i32 {
				ret 5
				7
			}
			""";
		Assert.Equal(KValue.I32(5), Run(source, "f"));
	}

	[Fact]
	public void Compound_assignment_uses_the_local_slot()
	{
		var result = Run(
			"""
			fun f() -> i32 {
				i32 x = 1
				x += 4
				x
			}
			""",
			"f");

		Assert.Equal(KValue.I32(5), result);
	}

	[Fact]
	public void Main_is_the_entry_point()
	{
		var result = Compile("fun main { }");
		Assert.Empty(result.Diagnostics);
		Assert.True(result.Module.EntryPoint >= 0);
		Assert.Equal("main", result.Module.FunctionPool[result.Module.EntryPoint].Name);
	}

	[Fact]
	public void Builtin_call_is_reported_as_KRN2201()
	{
		var result = Compile("fun main { println(1) }");
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "KRN2201");
	}

	[Fact]
	public void Numeric_widening_is_emitted_as_conversion()
	{
		const string source = "fun f(i64 x) -> i64 { x + 1 }";
		Assert.Equal(KValue.I64(42), Run(source, "f", KValue.I64(41)));
	}

	[Fact]
	public void Condition_loop_accumulates()
	{
		var result = Run(
			"""
			fun f(i32 n) -> i32 {
				i32 total = 0
				i32 i = 0
				loop i < n {
					total += i
					i += 1
				}
				total
			}
			""",
			"f",
			KValue.I32(5));

		Assert.Equal(KValue.I32(10), result);
	}

	[Fact]
	public void Infinite_loop_exits_with_stop()
	{
		var result = Run(
			"""
			fun f() -> i32 {
				i32 i = 0
				loop {
					i += 1
					if i >= 5 { stop }
				}
				i
			}
			""",
			"f");

		Assert.Equal(KValue.I32(5), result);
	}

	[Fact]
	public void Do_while_loop_runs_at_least_once()
	{
		var result = Run(
			"""
			fun f() -> i32 {
				i32 i = 0
				loop { i += 1 }: i < 3
				i
			}
			""",
			"f");

		Assert.Equal(KValue.I32(3), result);
	}

	[Fact]
	public void Next_skips_the_rest_of_the_iteration()
	{
		var result = Run(
			"""
			fun f() -> i32 {
				i32 i = 0
				i32 total = 0
				loop i < 5 {
					i += 1
					if i == 3 { next }
					total += i
				}
				total
			}
			""",
			"f");

		Assert.Equal(KValue.I32(12), result);
	}

	[Fact]
	public void Loop_expression_with_list_result_reports_KRN2201()
	{
		var result = Compile("fun f { var xs = loop { 1 } }");
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "KRN2201");
	}

	[Fact]
	public void Match_literal_and_wildcard_choose_a_branch()
	{
		const string source = "fun f(i32 x) -> i32 { match x { 1 => 10; _ => 20 } }";
		Assert.Equal(KValue.I32(10), Run(source, "f", KValue.I32(1)));
		Assert.Equal(KValue.I32(20), Run(source, "f", KValue.I32(2)));
	}

	[Fact]
	public void Match_bool_branches()
	{
		const string source = "fun f(bool flag) -> i32 { match flag { true => 1; false => 0 } }";
		Assert.Equal(KValue.I32(1), Run(source, "f", KValue.True));
		Assert.Equal(KValue.I32(0), Run(source, "f", KValue.False));
	}

	[Fact]
	public void Match_binding_and_guard()
	{
		const string source =
			"fun f(i32 x) -> i32 { match x { v if v > 10 => 1; v => 0 } }";
		Assert.Equal(KValue.I32(1), Run(source, "f", KValue.I32(11)));
		Assert.Equal(KValue.I32(0), Run(source, "f", KValue.I32(3)));
	}

	[Fact]
	public void Targetless_match_uses_conditions()
	{
		const string source = "fun f(i32 x) -> i32 { match { x > 0 => 1; _ => 0 } }";
		Assert.Equal(KValue.I32(1), Run(source, "f", KValue.I32(5)));
		Assert.Equal(KValue.I32(0), Run(source, "f", KValue.I32(-5)));
	}

	[Fact]
	public void Literal_pattern_is_converted_to_the_subject_type()
	{
		const string source = "fun f(i64 x) -> i32 { match x { 5 => 1; _ => 0 } }";
		Assert.Equal(KValue.I32(1), Run(source, "f", KValue.I64(5)));
		Assert.Equal(KValue.I32(0), Run(source, "f", KValue.I64(6)));
	}

	[Fact]
	public void Match_decodes_string_and_char_literals()
	{
		const string strings = "fun f(str s) -> i32 { match s { \"a\\n\" => 1; _ => 0 } }";
		Assert.Equal(KValue.I32(1), Run(strings, "f", KValue.Str("a\n")));
		Assert.Equal(KValue.I32(0), Run(strings, "f", KValue.Str("a")));

		const string chars = "fun g(char c) -> i32 { match c { '\\u0041' => 1; _ => 0 } }";
		Assert.Equal(KValue.I32(1), Run(chars, "g", KValue.Char('A')));
		Assert.Equal(KValue.I32(0), Run(chars, "g", KValue.Char('B')));
	}

	[Fact]
	public void Match_type_patterns_bind_narrowed_values()
	{
		const string source =
			"""
			unit Circle(i32 r)
			unit Rect(i32 w)
			type Shape = [Circle, Rect]

			fun circle(i32 r) -> Shape { Circle(r) }

			fun f(i32 n) -> i32 {
				Shape s = circle(n)
				match s { Circle c => c.r; Rect r => 100 + r.w }
			}
			""";
		Assert.Equal(KValue.I32(7), Run(source, "f", KValue.I32(7)));
	}

	[Fact]
	public void Match_structural_pattern_checks_named_fields()
	{
		const string source =
			"""
			unit Point(i32 x, i32 y)

			fun f(i32 x) -> i32 {
				Point p = Point(x, 0)
				match p { Point(x: 0, y: var y) => y; _ => -1 }
			}
			""";
		Assert.Equal(KValue.I32(0), Run(source, "f", KValue.I32(0)));
		Assert.Equal(KValue.I32(-1), Run(source, "f", KValue.I32(3)));
	}

	[Fact]
	public void Match_positional_pattern_binds_fields()
	{
		const string source =
			"""
			unit Point(i32 x, i32 y)

			fun f(i32 x) -> i32 {
				Point p = Point(x, 0)
				match p { Point(a, b) => a + b; _ => -1 }
			}
			""";
		Assert.Equal(KValue.I32(4), Run(source, "f", KValue.I32(4)));
	}

	[Fact]
	public void Match_without_a_matching_arm_panics()
	{
		var result = Compile("fun f(i32 x) -> i32 { match x { 1 => 1 } }");
		var panic = Assert.Throws<KernPanic>(() => new Interpreter(result.Module).Run("f", KValue.I32(2)));
		Assert.Contains("match", panic.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Unit_primary_constructor_and_method_use_the_receiver()
	{
		const string source =
			"""
			unit Point(i32 x) {
				fun get() -> i32 { $x }
			}

			fun f() -> i32 {
				Point p = Point(7)
				p.get()
			}
			""";

		Assert.Equal(KValue.I32(7), Run(source, "f"));
	}

	[Fact]
	public void Unit_field_assignment_updates_the_local_value()
	{
		const string source =
			"""
			unit Point(i32 x)

			fun f() -> i32 {
				Point p = Point(1)
				p.x = 5
				p.x
			}
			""";

		Assert.Equal(KValue.I32(5), Run(source, "f"));
	}

	[Fact]
	public void Auto_property_backing_field_reads_and_writes()
	{
		const string source =
			"""
			unit P(i32 seed) {
				i32 v { get; set }
			}

			fun f() -> i32 {
				P p = P(0)
				p.v = 3
				p.v
			}
			""";

		Assert.Equal(KValue.I32(3), Run(source, "f"));
	}

	[Fact]
	public void Computed_property_getter_is_called()
	{
		const string source =
			"""
			unit P(i32 x) {
				i32 d { get $x * 2 }
			}

			fun f() -> i32 {
				P p = P(4)
				p.d
			}
			""";

		Assert.Equal(KValue.I32(8), Run(source, "f"));
	}

	[Fact]
	public void Computed_property_setter_is_called()
	{
		const string source =
			"""
			unit P(i32 x) {
				i32 d {
					get $x
					set(value) $x = value * 2
				}
			}

			fun f() -> i32 {
				P p = P(1)
				p.d = 5
				p.x
			}
			""";

		Assert.Equal(KValue.I32(10), Run(source, "f"));
	}

	[Fact]
	public void Unit_copy_keeps_independent_field_values()
	{
		const string source =
			"""
			unit Point(i32 x)

			fun f() -> i32 {
				Point a = Point(1)
				Point b = a
				b.x = 5
				a.x
			}
			""";

		Assert.Equal(KValue.I32(1), Run(source, "f"));
	}

	[Fact]
	public void Nil_truthiness_follows_the_nil_value()
	{
		var source =
			"""
			fun f() -> i32 {
				i32? x = nil
				if x { 1 } else { 2 }
			}
			""";

		Assert.Equal(KValue.I32(2), Run(source, "f"));
	}

	[Fact]
	public void Truthiness_narrows_a_present_value()
	{
		var source =
			"""
			fun f() -> i32 {
				i32? x = 7
				if x { x + 1 } else { 0 }
			}
			""";

		Assert.Equal(KValue.I32(8), Run(source, "f"));
	}

	[Fact]
	public void Negated_truthiness_accepts_nil()
	{
		var source =
			"""
			fun f() -> i32 {
				i32? x = nil
				if !x { 1 } else { 2 }
			}
			""";

		Assert.Equal(KValue.I32(1), Run(source, "f"));
	}

	[Fact]
	public void Nil_equality_is_evaluated_at_runtime()
	{
		var nilSource =
			"""
			fun f() -> bool {
				i32? x = nil
				x == nil
			}
			""";
		var valueSource =
			"""
			fun f() -> bool {
				i32? x = 5
				x == nil
			}
			""";

		Assert.Equal(KValue.True, Run(nilSource, "f"));
		Assert.Equal(KValue.False, Run(valueSource, "f"));
	}

	[Fact]
	public void Early_return_narrows_at_runtime()
	{
		const string source =
			"""
			fun f(i32? x) -> i32 {
				if x == nil { ret 0 }
				x + 1
			}
			""";

		Assert.Equal(KValue.I32(5), Run(source, "f", KValue.I32(4)));
		Assert.Equal(KValue.I32(0), Run(source, "f", KValue.Nil));
	}

	[Fact]
	public void Type_test_runs_against_the_runtime_value()
	{
		const string source =
			"""
			fun f(object x) -> i32 {
				if x is i32 { x } else { 0 }
			}
			""";

		Assert.Equal(KValue.I32(7), Run(source, "f", KValue.I32(7)));
		Assert.Equal(KValue.I32(0), Run(source, "f", KValue.Str("x")));
	}

	[Fact]
	public void Union_type_test_runs_against_the_runtime_value()
	{
		const string source =
			"""
			type IntOrStr = [i32, str]
			fun f(IntOrStr x) -> i32 {
				if x is i32 { 1 } else { 0 }
			}
			""";

		Assert.Equal(KValue.I32(1), Run(source, "f", KValue.I32(7)));
		Assert.Equal(KValue.I32(0), Run(source, "f", KValue.Str("x")));
	}

	[Fact]
	public void Short_circuit_and_narrows_at_runtime()
	{
		const string source =
			"""
			fun f(i32? x) -> i32 {
				if x != nil && x > 0 { x } else { 0 }
			}
			""";

		Assert.Equal(KValue.I32(3), Run(source, "f", KValue.I32(3)));
		Assert.Equal(KValue.I32(0), Run(source, "f", KValue.Nil));
	}

	[Fact]
	public void None_comparison_follows_the_initial_state_and_nil()
	{
		const string source =
			"""
			fun f() -> i32 {
				i32? x
				i32 result = 0
				if x == none { result = result + 1 }
				if x == nil { result = result + 10 }
				x = nil
				if x == none { result = result + 100 }
				if x == nil { result = result + 1000 }
				result
			}
			""";

		Assert.Equal(KValue.I32(1001), Run(source, "f"));
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
}
