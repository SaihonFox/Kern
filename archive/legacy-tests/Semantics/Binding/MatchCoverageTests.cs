using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Xunit;

namespace Kern.Tests.Semantics.Binding;

/// <summary>Q12: покрытие match (KRN2110), недостижимые ветви (KRN2111) и коды паттернов (KRN2112–2114).</summary>
public sealed class MatchCoverageTests
{
	[Fact]
	public void Bool_exhaustive_with_true_and_false_has_no_warning()
	{
		var module = Bind("""
			fun f(bool flag) {
				match flag { true => 1; false => 0 }
			}
			""");
		Assert.Equal(2, SingleMatch(module, "f").Arms.Count);
	}

	[Fact]
	public void Bool_missing_false_warns_KRN2110()
	{
		var diagnostic = Assert.Single(Warnings("""
			fun f(bool flag) {
				match flag { true => 1 }
			}
			"""), diagnostic => diagnostic.Code == "KRN2110");
		Assert.False(diagnostic.IsError);
		Assert.Contains("false", diagnostic.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Union_with_all_variants_has_no_warning()
	{
		var module = Bind("""
			unit Circle(i32 radius)
			unit Rect(i32 w)
			type Shape = [Circle, Rect]
			fun f(Shape shape) {
				match shape { Circle c => c.radius; Rect r => r.w }
			}
			""");
		Assert.Equal(2, SingleMatch(module, "f").Arms.Count);
	}

	[Fact]
	public void Union_missing_variant_warns_KRN2110()
	{
		var diagnostic = Assert.Single(Warnings("""
			unit Circle(i32 radius)
			unit Rect(i32 w)
			type Shape = [Circle, Rect]
			fun f(Shape shape) {
				match shape { Circle c => c.radius }
			}
			"""), diagnostic => diagnostic.Code == "KRN2110");
		Assert.Contains("Rect", diagnostic.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Nullable_with_nil_and_type_pattern_has_no_warning()
	{
		var module = Bind("""
			unit Circle(i32 radius)
			fun f(Circle? c) {
				match c { nil => 0; Circle x => x.radius }
			}
			""");
		Assert.Equal(2, SingleMatch(module, "f").Arms.Count);
	}

	[Fact]
	public void Nullable_missing_nil_warns_KRN2110()
	{
		var diagnostic = Assert.Single(Warnings("""
			unit Circle(i32 radius)
			fun f(Circle? c) {
				match c { Circle x => x.radius }
			}
			"""), diagnostic => diagnostic.Code == "KRN2110");
		Assert.Contains("nil", diagnostic.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Guarded_wildcard_does_not_exhaust_the_match()
	{
		var diagnostic = Assert.Single(Warnings("""
			fun f(bool flag) {
				match flag { true => 1; _ if flag => 0 }
			}
			"""), diagnostic => diagnostic.Code == "KRN2110");
		Assert.Contains("false", diagnostic.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Arm_after_wildcard_warns_KRN2111_on_that_arm()
	{
		var source = """
			fun f(i32 x) {
				match x { _ => 1; 1 => 2 }
			}
			""";
		var (module, diagnostics) = BindWithDiagnostics(source);
		var arm = SingleMatch(module, "f").Arms[1];
		var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2111");
		Assert.False(diagnostic.IsError);
		Assert.Equal(arm.Span, diagnostic.Span);
	}

	[Fact]
	public void Duplicate_literal_warns_KRN2111()
	{
		var source = """
			fun f(i32 x) {
				match x { 1 => 1; 1 => 2; _ => 0 }
			}
			""";
		var (module, diagnostics) = BindWithDiagnostics(source);
		var arm = SingleMatch(module, "f").Arms[1];
		var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2111");
		Assert.Equal(arm.Span, diagnostic.Span);
	}

	[Fact]
	public void Duplicate_type_pattern_warns_KRN2111()
	{
		var source = """
			unit Circle(i32 radius)
			unit Rect(i32 w)
			type Shape = [Circle, Rect]
			fun f(Shape shape) {
				match shape { Circle a => a.radius; Circle b => b.radius; _ => 0 }
			}
			""";
		var (module, diagnostics) = BindWithDiagnostics(source);
		var arm = SingleMatch(module, "f").Arms[1];
		var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "KRN2111");
		Assert.Equal(arm.Span, diagnostic.Span);
	}

	[Fact]
	public void Targetless_match_has_no_coverage_warning()
	{
		var module = Bind("""
			fun f(i32 x) {
				match { x > 0 => 1; _ => 0 }
			}
			""");
		Assert.Equal(2, SingleMatch(module, "f").Arms.Count);
	}

	[Fact]
	public void Literal_type_mismatch_reports_KRN2112()
	{
		var diagnostics = Diagnostics("""
			fun f(i32 x) {
				match x { "a" => 1; _ => 0 }
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2112" && diagnostic.IsError);
	}

	[Fact]
	public void Nil_on_non_nullable_subject_reports_KRN2112()
	{
		var diagnostics = Diagnostics("""
			fun f(i32 x) {
				match x { nil => 1; _ => 0 }
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2112" && diagnostic.IsError);
	}

	[Fact]
	public void Constructor_pattern_on_class_reports_KRN2113()
	{
		var diagnostics = Diagnostics("""
			type Point(i32 x, i32 y)
			fun f(Point p) {
				match p { Point(a, b) => a; _ => 0 }
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2113" && diagnostic.IsError);
	}

	[Fact]
	public void Unknown_member_in_structural_pattern_reports_KRN2113()
	{
		var diagnostics = Diagnostics("""
			unit Point(i32 x, i32 y)
			fun f(Point p) {
				match p { Point(x: 0, z: 1) => 0; _ => 1 }
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2113" && diagnostic.IsError);
	}

	[Fact]
	public void Named_rest_in_structural_pattern_reports_KRN2114()
	{
		var diagnostics = Diagnostics("""
			unit Point(i32 x, i32 y)
			fun f(Point p) {
				match p { Point(x: 0, ..rest) => 0; _ => 1 }
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2114" && diagnostic.IsError);
	}

	[Fact]
	public void Range_pattern_with_non_literal_bound_reports_KRN2114()
	{
		var diagnostics = Diagnostics("""
			fun f(i32 x, i32 n) {
				match x { 1..=n => 1; _ => 0 }
			}
			""");
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2114" && diagnostic.IsError);
	}

	private static IReadOnlyList<Diagnostic> Warnings(string source) =>
		[.. Diagnostics(source).Where(diagnostic => !diagnostic.IsError)];

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
