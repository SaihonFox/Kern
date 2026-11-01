using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary><c>kern.events</c> (spec round 16): <c>event&lt;T&gt;</c> и <c>signal</c>.</summary>
public sealed class EventsModuleTests
{
	[Fact]
	public void Event_subscribe_emit_and_unsubscribe()
	{
		const string source = """
			type Button {
				event<str> clicked = event<str>()

				fun press(str who) {
					clicked.emit(who)
				}
			}

			fun run() -> i32 {
				var b = Button()
				list<str> seen = []
				var log = fun(str who) { seen.add(who) }
				b.clicked += log
				b.clicked += fun(str who) { seen.add(who + "!") }
				b.press("Аня")
				b.clicked -= log
				b.press("Боря")
				seen.len
			}

			fun first() -> str {
				var b = Button()
				list<str> seen = []
				b.clicked += fun(str who) { seen.add(who) }
				b.press("Аня")
				seen[0]
			}
			""";

		Assert.Equal(KValue.I32(3), StdPipeline.Run(source, "run"));
		Assert.Equal(KValue.Str("Аня"), StdPipeline.Run(source, "first"));
	}

	[Fact]
	public void Signal_without_argument_counts_and_clears()
	{
		const string source = """
			fun run() -> i32 {
				signal closed = signal()
				i32 n = 0
				closed += fun { n = n + 1 }
				closed += fun { n = n + 10 }
				closed.emit()
				closed.clear()
				closed.emit()
				n
			}
			""";

		Assert.Equal(KValue.I32(11), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Event_len_reports_subscribers()
	{
		const string source = """
			fun run() -> i32 {
				event<i32> changed = event<i32>()
				changed += fun(i32 _) { }
				changed += fun(i32 _) { }
				changed -= fun(i32 _) { }
				changed.len
			}
			""";

		Assert.Equal(KValue.I32(2), StdPipeline.Run(source, "run"));
	}
}
