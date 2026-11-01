using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>Q23: объявленный <c>op ==</c> типа важнее структурного/ссылочного сравнения.</summary>
public sealed class EqualityOperatorTests
{
	[Fact]
	public void Class_equality_uses_the_declared_operator()
	{
		const string source = """
			type Point(i32 x) {
				op ==(Point other) -> bool $x == other.x
			}

			fun equal() -> bool Point(1) == Point(1)
			fun not_equal() -> bool Point(1) != Point(2)
			fun negated() -> bool Point(1) != Point(1)
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "equal"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "not_equal"));
		Assert.Equal(KValue.Bool(false), StdPipeline.Run(source, "negated"));
	}

	[Fact]
	public void Dictionary_keys_use_declared_hash_and_equality()
	{
		const string source = """
			type Point(i32 x) {
				op hash() -> u64 1u64
				op ==(Point other) -> bool $x == other.x
			}

			fun run() -> str {
				dict<Point, str> values = []
				values[Point(1)] = "a"
				values[Point(2)] = "b"
				values[Point(1)] = "c"
				f"${values.len}/${values[Point(1)]}/${values[Point(2)]}"
			}
			""";

		Assert.Equal(KValue.Str("2/c/b"), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Dictionary_keys_with_only_equality_still_match()
	{
		const string source = """
			type Key(i32 x) {
				op ==(Key other) -> bool $x == other.x
			}

			fun run() -> str {
				dict<Key, i32> values = []
				values[Key(7)] = 42
				f"${values.len}/${values[Key(7)]}"
			}
			""";

		Assert.Equal(KValue.Str("1/42"), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Unit_equality_can_override_the_structural_default()
	{
		const string source = """
			unit Money(i32 cents) {
				op ==(Money other) -> bool $cents == other.cents
			}

			fun equal() -> bool Money(100) == Money(100)
			fun different() -> bool Money(100) != Money(101)
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "equal"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "different"));
	}
}
