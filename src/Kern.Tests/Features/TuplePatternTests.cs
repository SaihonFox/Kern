using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Паттерны кортежей (spec §11): <c>(p1, p2)</c> в match и справа от <c>is</c>;
/// кортеж из связываний считается покрывающим все значения.
/// </summary>
public sealed class TuplePatternTests
{
	[Fact]
	public void Tuple_patterns_select_arms_and_bind_items()
	{
		const string source = """
			fun both_literals() -> i32 {
				var t = (0, 0)
				match t {
					(0, 0) => 1
					(0, var y) => y + 2
					(var x, 0) => x + 3
					(var x, var y) => x + y + 4
				}
			}

			fun second_bound() -> i32 {
				var t = (0, 7)
				match t {
					(0, 0) => 1
					(0, var y) => y + 2
					(var x, 0) => x + 3
					(var x, var y) => x + y + 4
				}
			}

			fun first_bound() -> i32 {
				var t = (5, 0)
				match t {
					(0, 0) => 1
					(0, var y) => y + 2
					(var x, 0) => x + 3
					(var x, var y) => x + y + 4
				}
			}

			fun fallback() -> i32 {
				var t = (2, 3)
				match t {
					(0, 0) => 1
					(0, var y) => y + 2
					(var x, 0) => x + 3
					(var x, var y) => x + y + 4
				}
			}
			""";

		Assert.Equal(KValue.I32(1), StdPipeline.Run(source, "both_literals"));
		Assert.Equal(KValue.I32(9), StdPipeline.Run(source, "second_bound"));
		Assert.Equal(KValue.I32(8), StdPipeline.Run(source, "first_bound"));
		Assert.Equal(KValue.I32(9), StdPipeline.Run(source, "fallback"));
	}

	[Fact]
	public void Tuple_pattern_works_on_the_right_of_is()
	{
		const string source = """
			fun matches() -> i32 {
				var t = (1, 42)
				if t is (1, var second) {
					second
				} else {
					0
				}
			}

			fun no_match() -> i32 {
				var t = (2, 42)
				if t is (1, var second) {
					second
				} else {
					0
				}
			}
			""";

		Assert.Equal(KValue.I32(42), StdPipeline.Run(source, "matches"));
		Assert.Equal(KValue.I32(0), StdPipeline.Run(source, "no_match"));
	}

	[Fact]
	public void Binding_only_tuple_arm_counts_as_catch_all()
	{
		const string source = """
			fun classify() -> i32 {
				var t = (1, 2)
				match t {
					(0, 0) => 0
					(var x, var y) => x + y
				}
			}
			""";

		var module = StdPipeline.CompileOrNull(source, out var diagnostics);
		Assert.NotNull(module);
		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "KRN2110");
	}

	[Fact]
	public void Partial_tuple_match_still_reports_missing_coverage()
	{
		const string source = """
			fun classify() -> i32 {
				var t = (1, 2)
				match t {
					(0, 0) => 0
					(0, var y) => y
				}
			}
			""";

		Assert.NotNull(StdPipeline.CompileOrNull(source, out var diagnostics));
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2110");
	}
}
