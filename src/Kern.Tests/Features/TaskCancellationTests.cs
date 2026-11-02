using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Отмена awaited-задачи: <c>Resume</c> при ошибке/отмене обязан завершить внешний
/// <c>task&lt;T&gt;</c>, иначе <c>t.done</c> навсегда остаётся <c>false</c>
/// (пример 29 висел на этом в Release).
/// </summary>
public sealed class TaskCancellationTests
{
	[Fact]
	public void Canceled_awaited_task_completes_the_outer_task()
	{
		const string source = """
			use kern.tasks[cancel_token]

			async fun slow(cancel_token tok) -> i32 {
				await tok.delay(500)
				1
			}

			async fun check() -> bool {
				var tok = cancel_token()
				var t = slow(tok)
				tok.cancel_after(1)
				loop i32 i in 0..200 {
					if t.done { ret true }
					await delay(5)
				}
				false
			}
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.RunAsync(source, "check"));
	}
}
