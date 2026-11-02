using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Цепочки сравнений (spec §3): <c>0 &lt; x &lt; 10</c> — это
/// <c>0 &lt; x &amp;&amp; x &lt; 10</c> с однократным вычислением среднего операнда
/// и коротким замыканием.
/// </summary>
[Collection("timing")]
public sealed class ComparisonChainTests
{
	[Fact]
	public void Chain_compares_every_adjacent_pair()
	{
		const string source = """
			fun inside() -> bool 0 < 5 < 10
			fun below() -> bool 0 < -1 < 10
			fun above() -> bool 0 < 15 < 10
			fun long_chain() -> bool 1 < 2 < 3 < 4 < 5
			fun descending() -> bool 5 > 4 > 3 > 2
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "inside"));
		Assert.Equal(KValue.Bool(false), StdPipeline.Run(source, "below"));
		Assert.Equal(KValue.Bool(false), StdPipeline.Run(source, "above"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "long_chain"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "descending"));
	}

	[Fact]
	public void Parenthesized_comparison_is_not_a_chain()
	{
		const string source = """
			fun nested() -> bool (0 < 5) == true
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "nested"));
	}

	[Fact]
	public void Middle_operand_is_evaluated_once()
	{
		const string source = """
			type Counter {
				#i32 calls = 0
				#fun next() -> i32 {
					calls += 1
					calls
				}
			}

			fun once() -> i32 {
				bool ok = 0 < Counter.next() < 10
				Counter.calls
			}
			""";

		Assert.Equal(KValue.I32(1), StdPipeline.Run(source, "once"));
	}

	[Fact]
	public void Chain_short_circuits_after_a_failed_comparison()
	{
		const string source = """
			type Counter {
				#i32 calls = 0
				#fun next() -> i32 {
					calls += 1
					100
				}
			}

			fun short() -> i32 {
				bool ok = 10 < 5 < Counter.next() < 200
				Counter.calls
			}
			""";

		// 10 < 5 ложно, поэтому ни next(), ни правые операнды не вычисляются.
		Assert.Equal(KValue.I32(0), StdPipeline.Run(source, "short"));
	}
}
