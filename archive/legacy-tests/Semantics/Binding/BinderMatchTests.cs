using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Xunit;

namespace Kern.Tests.Semantics.Binding;

/// <summary>M3-E: базовый match — литералы, wildcard, bool, типы, guard и targetless-форма.</summary>
public sealed class BinderMatchTests
{
	[Fact]
	public void Literal_and_wildcard_patterns_bind()
	{
		var module = Bind("""
			fun f(i32 x) {
				match x { 1 => "one"; _ => "other" }
			}
			""");
		var match = SingleMatch(module, "f");

		Assert.NotNull(match.Target);
		Assert.Equal(PrimitiveType.I32, match.Target!.Type);
		Assert.Equal(2, match.Arms.Count);
		Assert.Equal(PrimitiveType.Str, match.Type);

		var literal = Assert.IsType<BoundLiteralPattern>(match.Arms[0].Pattern);
		Assert.Equal(PrimitiveType.I32, literal.Value.Type);
		Assert.IsType<BoundWildcardPattern>(match.Arms[1].Pattern);
		Assert.Empty(match.Arms[0].Bindings);
	}

	[Fact]
	public void Bool_patterns_bind_as_literals()
	{
		var module = Bind("""
			fun f(bool flag) {
				match flag { true => "yes"; false => "no" }
			}
			""");
		var match = SingleMatch(module, "f");

		var yes = Assert.IsType<BoundLiteralPattern>(match.Arms[0].Pattern);
		var no = Assert.IsType<BoundLiteralPattern>(match.Arms[1].Pattern);
		Assert.Equal(true, Assert.IsType<BoundLiteralExpression>(yes.Value).Value);
		Assert.Equal(false, Assert.IsType<BoundLiteralExpression>(no.Value).Value);
		Assert.Equal(PrimitiveType.Str, match.Type);
	}

	[Fact]
	public void Type_patterns_narrow_union_alternatives()
	{
		var module = Bind("""
			unit Circle(i32 radius)
			unit Rect(i32 w)
			type Shape = [Circle, Rect]
			fun f(Shape shape) {
				match shape { Circle c => c.radius; Rect r => r.w }
			}
			""");
		var match = SingleMatch(module, "f");

		Assert.Equal(PrimitiveType.I32, match.Type);
		var circle = Assert.IsType<BoundTypePattern>(match.Arms[0].Pattern);
		Assert.Equal("Circle", circle.Type.ToString());
		var binding = Assert.IsType<BoundBindingPattern>(circle.Binding);
		Assert.Equal("c", binding.Name);
		Assert.Equal("Circle", binding.Type.ToString());

		var rect = Assert.IsType<BoundTypePattern>(match.Arms[1].Pattern);
		Assert.Equal("Rect", rect.Type.ToString());
		Assert.NotEqual(circle.Binding, rect.Binding);
	}

	[Fact]
	public void Guard_binds_after_pattern_and_must_be_bool()
	{
		var module = Bind("""
			fun f(i32 score) {
				match score { v if v > 90 => "A"; _ => "B" }
			}
			""");
		var match = SingleMatch(module, "f");

		var arm = match.Arms[0];
		Assert.IsType<BoundBindingPattern>(arm.Pattern);
		Assert.Equal("v", Assert.Single(arm.Bindings).Name);
		var guard = Assert.IsType<BoundBinaryExpression>(arm.Guard);
		Assert.Equal(PrimitiveType.Bool, guard.Type);
		Assert.Equal(PrimitiveType.Str, match.Type);
	}

	[Fact]
	public void Targetless_match_binds_expression_guards()
	{
		var module = Bind("""
			fun f(i32 x) {
				match { x > 0 => "pos"; _ => "neg" }
			}
			""");
		var match = SingleMatch(module, "f");

		Assert.Null(match.Target);
		var guard = Assert.IsType<BoundGuardPattern>(match.Arms[0].Pattern);
		Assert.Equal(PrimitiveType.Bool, guard.Condition.Type);
		Assert.IsType<BoundWildcardPattern>(match.Arms[1].Pattern);
		Assert.Equal(PrimitiveType.Str, match.Type);
	}

	[Fact]
	public void Structural_unit_pattern_binds_named_fields()
	{
		var module = Bind("""
			unit Point(i32 x, i32 y)
			fun f(Point p) {
				match p { Point(x: 0, y: var y) => y; _ => 0 }
			}
			""");
		var match = SingleMatch(module, "f");

		var structural = Assert.IsType<BoundStructuralPattern>(match.Arms[0].Pattern);
		Assert.Equal("Point", structural.Type.ToString());
		Assert.Equal(2, structural.Fields.Count);
		Assert.IsType<BoundLiteralPattern>(structural.Fields[0].Pattern);
		Assert.Equal("y", Assert.Single(match.Arms[0].Bindings).Name);
	}

	[Fact]
	public void Incompatible_literal_pattern_reports_KRN2112()
	{
		var diagnostics = Diagnostics("""
			fun f(i32 x) {
				match x { "a" => 1; _ => 0 }
			}
			""");
		var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2112");
		Assert.True(diagnostic.IsError);
	}

	[Fact]
	public void Range_pattern_binds_as_bound_range()
	{
		var module = Bind("""
			fun f(i32 x) {
				match x { 1..=5 => 1; _ => 0 }
			}
			""");
		var match = SingleMatch(module, "f");

		var range = Assert.IsType<BoundRangePattern>(match.Arms[0].Pattern);
		Assert.True(range.Inclusive);
		Assert.Equal(PrimitiveType.I32, range.Type);
		Assert.Equal(2, match.Arms.Count);
	}

	[Fact]
	public void Rest_pattern_reports_KRN2114()
	{
		var diagnostics = Diagnostics("""
			fun f(i32 x) {
				match x { ..rest => 1; _ => 0 }
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2114" && diagnostic.IsError);
	}

	private static BoundMatchExpression SingleMatch(BoundModule module, string functionName)
	{
		var function = module.FindFunction(functionName)!;
		var statement = Assert.IsType<BoundExpressionStatement>(Assert.Single(function.Body.Statements));
		return Assert.IsType<BoundMatchExpression>(statement.Expression);
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
}
