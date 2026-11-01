using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary><c>kern.test</c>, моки (spec round 9): заглушка абстрактного <c>@type</c>.</summary>
public sealed class MockModuleTests
{
	private const string Source = """
		@type Mailer {
			@fun send(str to, str body) -> bool
			@fun ping() -> i32
		}

		fun notify_all(Mailer m, list<str> users) -> i32 {
			i32 sent = 0
			loop var user in users {
				if m.send(user, "Привет, " + user) { sent += 1 }
			}
			sent
		}
		""";

	[Fact]
	public void Returns_and_returns_for_change_the_result_and_are_recorded()
	{
		string source = Source + """

			fun run() -> i32 {
				Mailer m = mock.create<Mailer>()
				mock.returns(m, "send", true)
				mock.returns_for(m, "send", ["bob", "Привет, bob"], false)
				i32 sent = notify_all(m, ["ann", "bob", "eve"])
				mock.verify(m, "send", 3)
				mock.verify_with(m, "send", ["ann", "Привет, ann"], 1)
				mock.verify_never(m, "ping")
				sent
			}
			""";

		Assert.Equal(KValue.I32(2), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Custom_handler_and_default_values()
	{
		string source = Source + """

			fun run() -> i32 {
				Mailer m = mock.create<Mailer>()
				mock.handle(m, "ping", fun (list<object> args) -> i32 42)
				i32 first = m.ping()
				i32 calls = mock.calls(m, "ping")
				mock.reset_calls(m)
				i32 after = mock.calls(m, "ping")
				i32 fallback = if m.send("x", "y") { 100 } else { 200 }
				first + calls + after + fallback
			}
			""";

		Assert.Equal(KValue.I32(243), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Verify_failure_reports_a_russian_panic()
	{
		string source = Source + """

			fun run() {
				Mailer m = mock.create<Mailer>()
				mock.verify(m, "send", 1)
			}
			""";

		var panic = StdPipeline.RunPanics(source, "run");
		Assert.Contains("mock.verify", panic.Message, StringComparison.Ordinal);
	}
}
