using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Xunit;

namespace Kern.Tests.Semantics.Binding;

/// <summary>M3-C: циклы, stop/next и тип результата loop.</summary>
public sealed class BinderLoopTests
{
	[Fact]
	public void Condition_loop_binds_as_statement()
	{
		var module = Bind("""
			fun f {
				i32 n = 3
				loop n > 0 { n -= 1 }
			}
			""");
		var function = module.FindFunction("f")!;
		var loop = Assert.IsType<BoundLoopStatement>(function.Body.Statements[1]);

		Assert.False(loop.IsInfinite);
		Assert.False(loop.IsDoWhile);
		Assert.NotNull(loop.Condition);
		Assert.Equal(PrimitiveType.Void, loop.Body.Value?.Type ?? PrimitiveType.Void);
	}

	[Fact]
	public void Infinite_loop_is_a_statement_and_does_not_fall_through()
	{
		var module = Bind("fun f { loop { stop } }");
		var loop = Assert.IsType<BoundLoopStatement>(Assert.Single(module.FindFunction("f")!.Body.Statements));

		Assert.True(loop.IsInfinite);
		Assert.Null(loop.Condition);
		Assert.False(module.FindFunction("f")!.Body.FallsThrough);
	}

	[Fact]
	public void Do_while_checks_condition_after_the_body()
	{
		var module = Bind("""
			fun f {
				i32 n = 0
				loop { n += 1 }: n < 3
			}
			""");
		var function = module.FindFunction("f")!;
		var loop = Assert.IsType<BoundLoopStatement>(function.Body.Statements[1]);

		Assert.True(loop.IsDoWhile);
		Assert.NotNull(loop.Condition);
		Assert.Equal(PrimitiveType.Bool, loop.Condition!.Type);
	}

	[Fact]
	public void Loop_body_value_defines_list_element_type()
	{
		var module = Bind("fun f { var xs = loop { 1 } }");
		var local = Assert.IsType<BoundLocalDeclaration>(module.FindFunction("f")!.Body.Statements[0]);

		Assert.Equal(new BuiltinGenericType(BuiltinGenericKind.List, [PrimitiveType.I32]), local.Type);
		var loop = Assert.IsType<BoundLoopExpression>(local.Initializer);
		Assert.Equal(PrimitiveType.I32, loop.Body.Value?.Type);
	}

	[Fact]
	public void Loop_condition_must_be_bool()
	{
		var diagnostics = Diagnostics("fun f { loop 1 { } }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2102");
	}

	[Fact]
	public void Do_while_condition_must_be_bool()
	{
		var diagnostics = Diagnostics("fun f { loop { }: 1 }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2102");
	}

	[Fact]
	public void Stop_outside_loop_reports_KRN2106()
	{
		var diagnostics = Diagnostics("fun f { stop }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2106");
	}

	[Fact]
	public void Next_outside_loop_reports_KRN2106()
	{
		var diagnostics = Diagnostics("fun f { next }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2106");
	}

	[Fact]
	public void Stop_and_next_inside_nested_loops_bind()
	{
		var module = Bind("""
			fun f {
				loop {
					loop {
						next
					}
					stop
				}
			}
			""");
		var outer = Assert.IsType<BoundLoopStatement>(Assert.Single(module.FindFunction("f")!.Body.Statements));
		var inner = Assert.IsType<BoundLoopStatement>(outer.Body.Statements[0]);
		Assert.IsType<BoundNextStatement>(Assert.Single(inner.Body.Statements));
		Assert.IsType<BoundStopStatement>(outer.Body.Statements[1]);
	}

	[Fact]
	public void Loop_body_has_its_own_scope()
	{
		var diagnostics = Diagnostics("fun f { loop { i32 x = 1 } x }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2103");
	}

	[Fact]
	public void Do_while_condition_cannot_see_body_locals()
	{
		var diagnostics = Diagnostics("fun f { loop { i32 x = 1 }: x > 0 }");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2103");
	}

	[Fact]
	public void Nested_loop_shadowing_is_allowed()
	{
		var module = Bind("""
			fun f {
				i32 x = 1
				loop {
					i32 x = 2
					loop { x = 3 }
					stop
				}
			}
			""");
		Assert.NotNull(module.FindFunction("f"));
	}

	[Fact]
	public void Iteration_loop_over_a_list_binds_its_variable()
	{
		var module = Bind("""
			fun f() -> i32 {
				i32 sum = 0
				loop var i in [1, 2] {
					sum += i
				}
				sum
			}
			""");
		Assert.NotNull(module.FindFunction("f"));
	}

	[Fact]
	public void Condition_loop_body_value_is_converted_to_list_element_type()
	{
		var module = Bind("""
			fun f {
				i32 n = 1
				var xs = loop n > 0 { 42 }
			}
			""");
		var function = module.FindFunction("f")!;
		var local = Assert.IsType<BoundLocalDeclaration>(function.Body.Statements[1]);
		var loop = Assert.IsType<BoundLoopExpression>(local.Initializer);
		Assert.Equal(new BuiltinGenericType(BuiltinGenericKind.List, [PrimitiveType.I32]), local.Type);
		Assert.Equal(PrimitiveType.I32, Assert.IsType<PrimitiveType>(loop.Body.Value!.Type));
	}

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
