using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// <c>kern.threading</c> (spec round 24): потоки с общей памятью, канал, atomic,
/// mutex, потокобезопасные коллекции и общая ячейка.
/// </summary>
public sealed class ThreadingModuleTests
{
	[Fact]
	public void Worker_join_returns_result_and_captures_panic()
	{
		const string source = """
			fun square(i32 n) -> i32 n * n

			fun success() -> i32 {
				worker<i32> w = thread.spawn_with(square, 12)
				match w.join() {
					Ok(value) => value
					Err(_) => -1
				}
			}

			fun failed() -> bool {
				worker<i32> w = thread.spawn(fun -> i32 { panic("не вышло") })
				match w.join() {
					Ok(_) => false
					Err(message) => message == "не вышло"
				}
			}
			""";

		Assert.Equal(KValue.I32(144), StdPipeline.Run(source, "success"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "failed"));
	}

	[Fact]
	public void Channel_producer_and_consumer_share_values()
	{
		const string source = """
			fun producer(channel<i32> ch, i32 count) -> i32 {
				loop i32 i in 1..=count {
					ch.send(i)
				}
				ch.close()
				count
			}

			fun run() -> i32 {
				channel<i32> ch(2)
				worker<i32> p = thread.spawn_with(producer, ch, 5)
				i32 sum = 0
				loop {
					i32? value = ch.recv()
					if value == nil { stop }
					sum += value!
				}
				match p.join() {
					Ok(_) => {}
					Err(_) => { sum = -1 }
				}
				sum
			}
			""";

		Assert.Equal(KValue.I32(15), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Atomic_mutex_and_shared_memory_across_threads()
	{
		const string source = """
			fun hit(atomic counter, i32 times) -> i32 {
				loop 0..times { counter.increment() }
				times
			}

			fun run() -> i64 {
				atomic hits(0)
				list<worker<i32>> workers = []
				loop 0..4 {
					workers.add(thread.spawn_with(hit, hits, 1000))
				}
				loop var worker in workers {
					match worker.join() {
						Ok(_) => {}
						Err(_) => {}
					}
				}
				hits.value
			}
			""";

		Assert.Equal(KValue.I64(4000), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Concurrent_collections_and_shared_cell()
	{
		const string source = """
			fun run() -> i32 {
				conc_dict<str, i32> freq = conc_dict()
				freq.update("a", 0, fun(i32 n) -> i32 n + 1)
				freq.update("a", 0, fun(i32 n) -> i32 n + 1)
				freq.update("b", 0, fun(i32 n) -> i32 n + 1)
				conc_list<i32> list = conc_list()
				list.add(1)
				list.add(2)
				list.add_if_absent(2)
				conc_queue<i32> queue = conc_queue()
				queue.push(7)
				shared<str> status = shared("idle")
				status.set("done")
				freq.get("a")! * 100 + list.len * 10 + queue.pop()! + if status.get() == "done" { 1 } else { 0 }
			}
			""";

		Assert.Equal(KValue.I32(228), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Mutex_guard_and_run_hold_the_lock()
	{
		const string source = """
			fun run() -> i32 {
				mutex gate = mutex()
				var guard = gate.try_guard(10)
				if guard != nil { guard.dispose() }
				gate.run(fun -> i32 42)
			}
			""";

		Assert.Equal(KValue.I32(42), StdPipeline.Run(source, "run"));
	}
}
