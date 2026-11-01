using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary><c>kern.tasks</c> и <c>kern.parallel</c> (spec rounds 23/24).</summary>
[Collection("timing")]
public sealed class TasksModuleTests
{
	[Fact]
	public void Combinators_all_any_race_select_and_settled()
	{
		const string source = """
			async fun fetch(i32 id, i32 ms) -> i32 {
				await delay(ms)
				id * 10
			}

			async fun failing -> i32 {
				await delay(5)
				panic("не вышло")
				0
			}

			async fun run() -> bool {
				var all = await tasks.all([fetch(1, 30), fetch(2, 10), fetch(3, 20)])
				var any = await tasks.any([failing(), fetch(7, 20)])
				var race = await tasks.race([fetch(1, 50), fetch(2, 5)])
				var select = await tasks.select([fetch(1, 40), fetch(2, 5), fetch(3, 20)])
				var settled = await tasks.settled([fetch(4, 5), failing()])
				all[0] == 10 && all[1] == 20 && all[2] == 30 &&
					any == 70 && (race == 20 || race == 60) &&
					select >= 0 && select <= 2 && settled.len == 2
			}
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.RunAsync(source, "run"));
	}

	[Fact]
	public void Timeout_returns_err_and_retry_succeeds()
	{
		const string source = """
			async fun fetch(i32 id, i32 ms) -> i32 {
				await delay(ms)
				id * 10
			}

			async fun run() -> str {
				match await tasks.timeout(fetch(9, 500), 50) {
					Ok(value) => f"ok ${value}"
					Err(message) => message
				}
			}

			async fun retried() -> i32 {
				await tasks.retry(fun -> task<i32> { fetch(5, 1) }, 3, 1)
			}
			""";

		Assert.Equal(KValue.Str("таймаут"), StdPipeline.RunAsync(source, "run"));
		Assert.Equal(KValue.I32(50), StdPipeline.RunAsync(source, "retried"));
	}

	[Fact]
	public void Pipe_and_async_loop_deliver_values()
	{
		const string source = """
			async fun produce(pipe<i32> p) {
				loop i32 i in 0..5 { await p.send(i) }
				p.close()
			}

			async fun run() -> i32 {
				var p = pipe<i32>(2)
				tasks.run(async fun { await produce(p) })
				i32 sum = 0
				async loop var value in pipe_stream(p) {
					sum += value
				}
				sum
			}
			""";

		Assert.Equal(KValue.I32(10), StdPipeline.RunAsync(source, "run"));
	}

	[Fact]
	public void Parallel_map_reduce_and_pool()
	{
		const string source = """
			fun square(i32 x) -> i32 x * x

			async fun run() -> i32 {
				var mapped = await parallel.map([1, 2, 3, 4, 5], square)
				var total = await parallel.reduce(mapped, 0, fun(i32 a, i32 b) -> i32 a + b)
				var workers = pool(2)
				var submitted = await workers.submit(square, 12)
				workers.close()
				mapped.len * 100 + total + submitted
			}
			""";

		// 5*100 + 55 + 144 = 699
		Assert.Equal(KValue.I32(699), StdPipeline.RunAsync(source, "run"));
	}

	[Fact]
	public void Cancel_token_stops_a_timer()
	{
		const string source = """
			async fun ticker(cancel_token token) -> i32 {
				i32 n = 0
				loop {
					token.check()
					await token.delay(5)
					n += 1
				}
				n
			}

			async fun run() -> bool {
				var token = cancel_token()
				var tickerTask = ticker(token)
				token.cancel_after(20)
				loop {
					if tickerTask.done { stop }
					await delay(5)
				}
				token.cancelled && tickerTask.done && (tickerTask.failed || tickerTask.cancelled)
			}
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.RunAsync(source, "run"));
	}
}
