using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>M14, `is`-паттерны: полный синтаксис match справа от `is`, привязки и smart casts.</summary>
public sealed class IsPatternTests
{
	[Fact]
	public void Type_pattern_reports_true_and_false()
	{
		const string source = """
			type Point(i32 x)

			fun yes() -> bool {
				var p = Point(1)
				p is Point
			}

			fun no() -> bool {
				Point? maybe = nil
				maybe is Point
			}
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "yes"));
		Assert.Equal(KValue.Bool(false), StdPipeline.Run(source, "no"));
	}

	[Fact]
	public void Constructor_binding_is_visible_in_the_then_branch_and_after_and()
	{
		const string source = """
			unit Point(i32 x, i32 y)

			fun branch() -> i32 {
				var p = Point(7, 2)
				if p is Point(var a, _) {
					a
				} else {
					0
				}
			}

			fun conjunction() -> i32 {
				var p = Point(7, 2)
				if p is Point(var a, 2) && a > 5 {
					a
				} else {
					0
				}
			}
			""";

		Assert.Equal(KValue.I32(7), StdPipeline.Run(source, "branch"));
		Assert.Equal(KValue.I32(7), StdPipeline.Run(source, "conjunction"));
	}

	[Fact]
	public void Structural_binding_and_range_patterns_work()
	{
		const string source = """
			unit Point(i32 x, i32 y)

			fun structural() -> i32 {
				var p = Point(3, 2)
				if p is Point(x: var a, y: 2) {
					a
				} else {
					0
				}
			}

			fun range() -> str {
				var n = 3
				if n is 1..=5 {
					"in"
				} else {
					"out"
				}
			}
			""";

		Assert.Equal(KValue.I32(3), StdPipeline.Run(source, "structural"));
		Assert.Equal(KValue.Str("in"), StdPipeline.Run(source, "range"));
	}

	[Fact]
	public void Generic_and_nullable_patterns_smart_cast()
	{
		const string source = """
			fun generic() -> bool {
				list<i32> xs = [1, 2]
				xs is list<i32>
			}

			fun nullable(str? text) -> i32 {
				if text is str {
					text.len
				} else {
					0
				}
			}
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "generic"));
		Assert.Equal(KValue.I32(3), StdPipeline.Run(source, "nullable", KValue.Str("abc")));
	}

	[Fact]
	public void Negation_and_nil_patterns_work_without_bindings()
	{
		const string source = """
			fun negated() -> bool {
				var value = 1
				value !is str
			}

			fun nil_pattern(str? text) -> bool {
				text is nil
			}
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "negated"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "nil_pattern", KValue.Nil));
	}

	[Fact]
	public void Var_binding_always_matches()
	{
		const string source = """
			fun run() -> i32 {
				var value = 41
				if value is var copy {
					copy + 1
				} else {
					0
				}
			}
			""";

		Assert.Equal(KValue.I32(42), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Bindings_are_rejected_under_negation_and_or()
	{
		const string source = """
			unit Point(i32 x)

			fun negated() -> bool {				var p = Point(1)
				p !is Point(var a)
			}

			fun disjunction() -> bool {
				var p = Point(1)
				p is Point(var a) || p is Point
			}
			""";

		var module = StdPipeline.CompileOrNull(source, out var diagnostics);

		Assert.Null(module);
		Assert.Equal(2, diagnostics.Count(diagnostic =>
			diagnostic.Code == "KRN2101" &&
			diagnostic.Message.Contains("недоступны", StringComparison.Ordinal)));
	}
}
