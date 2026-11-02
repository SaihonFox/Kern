using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Генераторы и ленивая <c>seq&lt;T&gt;</c> (spec §6, раунд 27): вызов не исполняет тело,
/// <c>yield</c> отдаёт элемент, <c>map/filter/take/skip</c> ленивые, <c>loop</c> идёт по
/// протоколу <c>iter&lt;T&gt;</c>.
/// </summary>
public sealed class GeneratorTests
{
	private const string Source = """
		fun naturals(i32 n) -> seq<i32> {
			loop i32 i in 0..n yield i
		}

		fun lazy_before() -> i32 {
			list<i32> seen = []
			fun gen() -> seq<i32> {
				seen.add(5)
				yield 5
			}
			seq<i32> s = gen()
			seen.len
		}

		fun touch_after() -> i32 {
			list<i32> seen = []
			fun gen() -> seq<i32> {
				seen.add(5)
				yield 5
			}
			seq<i32> s = gen()
			s.next()
			seen.len
		}

		fun take_three() -> str naturals(10).take(3).to_list().join(",")

		fun skipped() -> str naturals(10).skip(5).to_list().join(",")

		fun squares() -> str naturals(5).map(fun (i32 x) -> i32 x * x).to_list().join(",")

		fun evens() -> str naturals(10).filter(fun (i32 x) -> bool x % 2 == 0).to_list().join(",")

		fun total(i32 n) -> i32 {
			var sum = 0
			loop var x in naturals(n) {
				sum += x
			}
			sum
		}

		fun ends_with_nil() -> bool {
			seq<i32> s = naturals(1)
			s.next()
			s.next() == nil
		}
		""";

	[Fact]
	public void Seq_chains_are_lazy_and_ordered()
	{
		Assert.Equal(KValue.Str("0,1,2"), StdPipeline.Run(Source, "take_three"));
		Assert.Equal(KValue.Str("5,6,7,8,9"), StdPipeline.Run(Source, "skipped"));
		Assert.Equal(KValue.Str("0,1,4,9,16"), StdPipeline.Run(Source, "squares"));
		Assert.Equal(KValue.Str("0,2,4,6,8"), StdPipeline.Run(Source, "evens"));
		Assert.Equal(KValue.I32(10), StdPipeline.Run(Source, "total", KValue.I32(5)));
	}

	[Fact]
	public void Body_runs_only_when_the_sequence_is_consumed()
	{
		Assert.Equal(KValue.I32(0), StdPipeline.Run(Source, "lazy_before"));
		Assert.Equal(KValue.I32(1), StdPipeline.Run(Source, "touch_after"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(Source, "ends_with_nil"));
	}
}
