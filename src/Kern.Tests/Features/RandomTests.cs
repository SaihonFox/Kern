using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// <c>kern.random</c> (spec §16): криптостойкие диапазоны/байты и детерминированный
/// <c>rng</c> (одинаковый seed — одинаковая последовательность).
/// </summary>
public sealed class RandomTests
{
	private const string Source = """
		fun same_seed() -> bool {
			var a = random.rng(7)
			var b = random.rng(7)
			loop i32 i in 0..10 {
				if a.next_i32(100) != b.next_i32(100) { ret false }
			}
			true
		}

		fun secure_ranges() -> bool {
			loop i32 i in 0..100 {
				var x = random.i32(10)
				if x < 0 || x >= 10 { ret false }
				var y = random.i64(5)
				if y < 0 || y >= 5 { ret false }
				var z = random.f64()
				if z < 0.0 || z >= 1.0 { ret false }
			}
			true
		}

		fun shapes() -> str {
			var bytes = random.bytes(8)
			var hex = random.hex(8)
			var xs = [1, 2, 3, 4, 5]
			var shuffled = random.shuffle(xs)
			var picked = random.sample(xs, 3)
			f"${bytes.len},${hex.len},${shuffled.len},${picked.len},${random.choice(xs) in xs}"
		}

		fun rng_shuffle() -> bool {
			var r = random.rng(5)
			var shuffled = r.shuffle([1, 2, 3, 4, 5])
			shuffled.len == 5
		}
		""";

	[Fact]
	public void Deterministic_rng_repeats_with_the_same_seed_and_secure_values_stay_in_range()
	{
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(Source, "same_seed"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(Source, "secure_ranges"));
		Assert.Equal(KValue.Str("8,16,5,3,true"), StdPipeline.Run(Source, "shapes"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(Source, "rng_shuffle"));
	}

	[Fact]
	public void Invalid_ranges_and_empty_lists_panic()
	{
		Assert.Contains("диапазон", StdPipeline.RunPanics(
			"fun bad() { random.i32(0) }", "bad").Message);
		Assert.Contains("пустой список", StdPipeline.RunPanics(
			"fun bad() { random.choice(<i32>[]) }", "bad").Message);
		Assert.Contains("n должно быть", StdPipeline.RunPanics(
			"fun bad() { random.sample([1, 2], 3) }", "bad").Message);
		Assert.Contains("количество", StdPipeline.RunPanics(
			"fun bad() { random.bytes(-1) }", "bad").Message);
	}
}
