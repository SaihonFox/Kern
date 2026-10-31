using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Semantics.Types.Conversions;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Xunit;

namespace Kern.Tests.Semantics.Binding;

/// <summary>
/// M3-B: обязательное присваивание, smart casts для nullable/union, расхождение `never`
/// и предупреждение о недостижимом коде.
/// </summary>
public sealed class BinderFlowTests
{
	[Fact]
	public void Nil_literal_is_converted_to_nullable()
	{
		var module = Bind("fun f { i32? x = nil }");
		var local = Assert.IsType<BoundLocalDeclaration>(module.FindFunction("f")!.Body.Statements[0]);

		Assert.Equal(NullableType.Create(PrimitiveType.I32), local.Type);
		var conversion = Assert.IsType<BoundConversionExpression>(local.Initializer);
		Assert.Equal(ConversionKind.NilToNullable, conversion.Conversion.Kind);
		Assert.Equal(NullableType.Create(PrimitiveType.I32), conversion.Type);
		Assert.IsType<BoundNilExpression>(conversion.Operand);
	}

	[Fact]
	public void Nil_to_non_nullable_is_rejected()
	{
		var diagnostics = Diagnostics("fun f { i32 x = nil }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2102" && diagnostic.IsError);
	}

	[Fact]
	public void Var_cannot_infer_type_from_nil()
	{
		var diagnostics = Diagnostics("fun f { var x = nil }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2102" && diagnostic.IsError);
	}

	[Fact]
	public void Nil_binds_for_nullable_str_and_list()
	{
		var module = Bind("""
			fun f {
				str? s = nil
				list<i32>? xs = nil
			}
			""");
		var function = module.FindFunction("f")!;

		var text = Assert.IsType<BoundLocalDeclaration>(function.Body.Statements[0]);
		Assert.Equal(NullableType.Create(PrimitiveType.Str), text.Type);
		Assert.Equal(
			ConversionKind.NilToNullable,
			Assert.IsType<BoundConversionExpression>(text.Initializer).Conversion.Kind);

		var list = Assert.IsType<BoundLocalDeclaration>(function.Body.Statements[1]);
		Assert.Equal(
			NullableType.Create(new BuiltinGenericType(BuiltinGenericKind.List, [PrimitiveType.I32])),
			list.Type);
		Assert.Equal(
			ConversionKind.NilToNullable,
			Assert.IsType<BoundConversionExpression>(list.Initializer).Conversion.Kind);
	}

	[Fact]
	public void Nil_is_rejected_for_object()
	{
		var diagnostics = Diagnostics("fun f { object o = nil }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2102" && diagnostic.IsError);
	}

	[Fact]
	public void Nil_is_rejected_for_plain_str()
	{
		var diagnostics = Diagnostics("fun f { str s = nil }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2102" && diagnostic.IsError);
	}

	[Fact]
	public void Nil_comparison_is_rejected_for_object()
	{
		var diagnostics = Diagnostics("fun f(object o) { o == nil }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2101" && diagnostic.IsError);
	}

	[Fact]
	public void Nil_pattern_is_rejected_for_object()
	{
		var diagnostics = Diagnostics("""
			fun f(object o) {
				match o { nil => 1; _ => 0 }
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2112" && diagnostic.IsError);
	}

	[Fact]
	public void Unassigned_nullable_reads_as_none()
	{
		var module = Bind("""
			fun f {
				i32? x
				println(x)
			}
			""");
		var call = Assert.IsType<BoundCallExpression>(
			Assert.IsType<BoundExpressionStatement>(module.FindFunction("f")!.Body.Statements[1]).Expression);
		var argument = Assert.IsType<BoundConversionExpression>(call.Arguments[0]);
		var variable = Assert.IsType<BoundVariableExpression>(argument.Operand);

		Assert.Equal(NullableType.Create(PrimitiveType.I32), variable.Type);
	}

	[Fact]
	public void Truthiness_narrows_nullable_in_then_branch()
	{
		var module = Bind("""
			fun f(i32? x) -> i32 {
				if x { x + 1 } else { 0 }
			}
			""");
		var conditional = Assert.IsType<BoundIfExpression>(module.FindFunction("f")!.Body.Value);
		var condition = Assert.IsType<BoundTruthinessExpression>(conditional.Condition);
		Assert.False(condition.Negated);

		var addition = Assert.IsType<BoundBinaryExpression>(BranchValue(conditional.Then));
		Assert.Equal(PrimitiveType.I32, Assert.IsType<BoundVariableExpression>(addition.Left).Type);
	}

	[Fact]
	public void Negated_truthiness_narrows_after_the_if()
	{
		var module = Bind("""
			fun f(i32? source) -> i32 {
				i32? x = source
				if !x { x = 10 }
				x
			}
			""");
		var function = module.FindFunction("f")!;
		var conditional = Assert.IsType<BoundIfExpression>(
			Assert.IsType<BoundExpressionStatement>(function.Body.Statements[1]).Expression);
		Assert.True(Assert.IsType<BoundTruthinessExpression>(conditional.Condition).Negated);

		var value = Assert.IsType<BoundVariableExpression>(function.Body.Value);
		Assert.Equal(PrimitiveType.I32, value.Type);
	}

	[Fact]
	public void Nil_comparison_narrows_after_early_return()
	{
		var module = Bind("""
			fun f(i32? x) -> i32 {
				if x == nil { ret 0 }
				x + 1
			}
			""");
		var addition = Assert.IsType<BoundBinaryExpression>(module.FindFunction("f")!.Body.Value);
		Assert.Equal(PrimitiveType.I32, Assert.IsType<BoundVariableExpression>(addition.Left).Type);
	}

	[Fact]
	public void Not_nil_comparison_narrows_then_branch()
	{
		var module = Bind("""
			fun f(i32? x) -> i32 {
				if x != nil { x + 1 } else { 0 }
			}
			""");
		var conditional = Assert.IsType<BoundIfExpression>(module.FindFunction("f")!.Body.Value);
		var addition = Assert.IsType<BoundBinaryExpression>(BranchValue(conditional.Then));
		Assert.Equal(PrimitiveType.I32, Assert.IsType<BoundVariableExpression>(addition.Left).Type);
	}

	[Fact]
	public void Short_circuit_and_narrows_the_right_operand()
	{
		var module = Bind("""
			fun f(i32? x) -> i32 {
				if x != nil && x > 0 { x } else { 0 }
			}
			""");
		var conditional = Assert.IsType<BoundIfExpression>(module.FindFunction("f")!.Body.Value);
		var logical = Assert.IsType<BoundBinaryExpression>(conditional.Condition);
		var comparison = Assert.IsType<BoundBinaryExpression>(logical.Right);
		Assert.Equal(PrimitiveType.I32, Assert.IsType<BoundVariableExpression>(comparison.Left).Type);
	}

	[Fact]
	public void Short_circuit_or_narrows_the_right_operand()
	{
		var module = Bind("""
			fun f(i32? x) -> i32 {
				if x == nil || x > 0 { 0 } else { x }
			}
			""");
		var conditional = Assert.IsType<BoundIfExpression>(module.FindFunction("f")!.Body.Value);
		var logical = Assert.IsType<BoundBinaryExpression>(conditional.Condition);
		var comparison = Assert.IsType<BoundBinaryExpression>(logical.Right);
		Assert.Equal(PrimitiveType.I32, Assert.IsType<BoundVariableExpression>(comparison.Left).Type);

		var otherwise = Assert.IsType<BoundVariableExpression>(BranchValue(conditional.Else!));
		Assert.Equal(PrimitiveType.I32, otherwise.Type);
	}

	[Fact]
	public void Is_narrows_a_union_to_the_tested_alternative()
	{
		var module = Bind("""
			type IntOrStr = [i32, str]
			fun f(IntOrStr x) -> i32 {
				if x is i32 { x } else { 0 }
			}
			""");
		var conditional = Assert.IsType<BoundIfExpression>(module.FindFunction("f")!.Body.Value);
		var test = Assert.IsType<BoundTypeTestExpression>(conditional.Condition);
		Assert.False(test.Negated);

		var then = Assert.IsType<BoundVariableExpression>(BranchValue(conditional.Then));
		Assert.Equal(PrimitiveType.I32, then.Type);
	}

	[Fact]
	public void Is_removes_the_alternative_in_the_else_branch()
	{
		var module = Bind("""
			type IntOrStr = [i32, str]
			fun f(IntOrStr x) -> str {
				if x is i32 { "int" } else { x }
			}
			""");
		var conditional = Assert.IsType<BoundIfExpression>(module.FindFunction("f")!.Body.Value);
		var otherwise = Assert.IsType<BoundVariableExpression>(BranchValue(conditional.Else!));
		Assert.Equal(PrimitiveType.Str, otherwise.Type);
	}

	[Fact]
	public void Is_narrows_object_to_a_primitive()
	{
		var module = Bind("""
			fun f(object x) -> i32 {
				if x is i32 { x } else { 0 }
			}
			""");
		var conditional = Assert.IsType<BoundIfExpression>(module.FindFunction("f")!.Body.Value);
		var then = Assert.IsType<BoundVariableExpression>(BranchValue(conditional.Then));
		Assert.Equal(PrimitiveType.I32, then.Type);
	}

	[Fact]
	public void Assigned_in_both_branches_is_definitely_assigned()
	{
		var module = Bind("""
			fun f(bool c) -> i32 {
				i32 x
				if c { x = 1 } else { x = 2 }
				x
			}
			""");
		Assert.Equal(PrimitiveType.I32, module.FindFunction("f")!.Body.Value!.Type);
	}

	[Fact]
	public void Assigned_in_one_branch_is_not_definite()
	{
		var diagnostics = Diagnostics("""
			fun f(bool c) {
				i32 x
				if c { x = 1 }
				println(x)
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2105" && diagnostic.IsError);
	}

	[Fact]
	public void Single_assignment_allows_mutually_exclusive_branches()
	{
		Bind("""
			fun f(bool c) {
				i32! x
				if c { x = 1 } else { x = 2 }
			}
			""");
	}

	[Fact]
	public void Single_assignment_still_rejects_a_second_write_after_the_if()
	{
		var diagnostics = Diagnostics("""
			fun f(bool c) {
				i32! x
				if c { x = 1 } else { x = 2 }
				x = 3
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2105" && diagnostic.IsError);
	}

	[Fact]
	public void Divergent_if_does_not_need_a_value()
	{
		var module = Bind("""
			fun f(i32 c) -> i32 {
				if c > 0 { ret 1 } else { 2 }
			}
			""");
		var conditional = Assert.IsType<BoundIfExpression>(module.FindFunction("f")!.Body.Value);
		Assert.Equal(PrimitiveType.I32, conditional.Type);
	}

	[Fact]
	public void Panic_diverges_without_a_missing_return()
	{
		Bind("""
			fun f() -> i32 {
				panic("boom")
			}
			""");
	}

	[Fact]
	public void Code_after_return_is_unreachable_and_warned_once()
	{
		var diagnostics = Diagnostics("""
			fun f() -> i32 {
				ret 1
				2
				3
			}
			""");
		var warnings = diagnostics.Where(diagnostic => diagnostic.Code == "KRN2304").ToList();
		Assert.Single(warnings);
		Assert.False(warnings[0].IsError);
	}

	[Fact]
	public void Not_is_narrows_the_else_branch()
	{
		var module = Bind("""
			fun f(object x) -> i32 {
				if x !is i32 { 0 } else { x }
			}
			""");
		var conditional = Assert.IsType<BoundIfExpression>(module.FindFunction("f")!.Body.Value);
		var otherwise = Assert.IsType<BoundVariableExpression>(BranchValue(conditional.Else!));
		Assert.Equal(PrimitiveType.I32, otherwise.Type);
	}

	[Fact]
	public void Loop_condition_narrows_inside_the_body()
	{
		var module = Bind("""
			fun f(i32? x) {
				loop x != nil {
					x + 1
				}
			}
			""");
		var loop = Assert.IsType<BoundLoopStatement>(module.FindFunction("f")!.Body.Statements[0]);
		Assert.NotNull(loop.Condition);

		var addition = Assert.IsType<BoundBinaryExpression>(
			Assert.IsType<BoundExpressionStatement>(loop.Body.Statements[0]).Expression);
		Assert.Equal(PrimitiveType.I32, Assert.IsType<BoundVariableExpression>(addition.Left).Type);
	}

	[Fact]
	public void Loop_narrowing_does_not_survive_the_exit()
	{
		var module = Bind("""
			fun f(i32? x) {
				loop x != nil { stop }
				x
			}
			""");
		var value = Assert.IsType<BoundVariableExpression>(
			Assert.IsType<BoundExpressionStatement>(module.FindFunction("f")!.Body.Statements[1]).Expression);
		Assert.Equal(NullableType.Create(PrimitiveType.I32), value.Type);
	}

	[Fact]
	public void Loop_body_assignment_is_not_definite_after_the_loop()
	{
		var diagnostics = Diagnostics("""
			fun f(bool c) {
				i32 x
				loop c { x = 1; stop }
				println(x)
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2105" && diagnostic.IsError);
	}

	[Fact]
	public void Loop_with_stop_is_followed_by_reachable_code()
	{
		var diagnostics = Diagnostics("""
			fun f {
				loop { stop }
				1
			}
			""");
		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "KRN2304");
	}

	[Fact]
	public void Infinite_loop_without_stop_makes_the_rest_unreachable()
	{
		var diagnostics = Diagnostics("""
			fun f {
				loop { }
				1
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2304");
	}

	[Fact]
	public void Match_guard_narrows_the_arm_body()
	{
		var module = Bind("""
			fun f(i32? x) -> i32 {
				match x { m if x != nil => x + 1; _ => 0 }
			}
			""");
		var match = Assert.IsType<BoundMatchExpression>(module.FindFunction("f")!.Body.Value);
		var addition = Assert.IsType<BoundBinaryExpression>(match.Arms[0].Body);
		Assert.Equal(PrimitiveType.I32, Assert.IsType<BoundVariableExpression>(addition.Left).Type);
	}

	[Fact]
	public void Unreachable_nested_branches_do_not_repeat_the_warning()
	{
		var diagnostics = Diagnostics("""
			fun f() -> i32 {
				ret 0
				i32 x = 1
				if true { x } else { 2 }
			}
			""");
		Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2304");
	}

	[Fact]
	public void None_is_bound_as_the_internal_state()
	{
		var module = Bind("fun f() -> bool { none == none }");
		var comparison = Assert.IsType<BoundBinaryExpression>(module.FindFunction("f")!.Body.Value);
		Assert.Equal(PrimitiveType.None, Assert.IsType<BoundNoneExpression>(comparison.Left).Type);
		Assert.Equal(PrimitiveType.Bool, comparison.Type);
	}

	[Fact]
	public void None_can_be_compared_with_any_operand_type()
	{
		var module = Bind("""
			fun f(i32? x, str s) -> bool {
				if x != none && s != none { x == nil } else { false }
			}
			""");
		Assert.Equal(PrimitiveType.Bool, module.FindFunction("f")!.Body.Value!.Type);
	}

	[Fact]
	public void None_comparison_does_not_prove_non_nil()
	{
		var diagnostics = Diagnostics("fun f(i32? x) { if x != none { x + 1 } }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2101" && diagnostic.IsError);
	}

	[Fact]
	public void None_is_not_assignable()
	{
		var diagnostics = Diagnostics("fun f { i32? x = none }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2102" && diagnostic.IsError);
	}

	[Fact]
	public void Var_cannot_infer_type_from_none()
	{
		var diagnostics = Diagnostics("fun f { var x = none }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2102" && diagnostic.IsError);
	}

	private static BoundExpression BranchValue(BoundExpression branch) =>
		branch is BoundBlockExpression { Value: { } value } ? value : branch;

	private static BoundModule Bind(string source)
	{
		var (module, diagnostics) = BindWithDiagnostics(source);
		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.IsError);
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
}
