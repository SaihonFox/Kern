using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// <c>#[memo]</c> (spec §7): результат функции кэшируется по аргументам без ручного
/// <c>dict</c>; повторный вызов с теми же аргументами тело не выполняет.
/// </summary>
[Collection("timing")]
public sealed class MemoTests
{
	[Fact]
	public void Repeated_call_with_equal_arguments_uses_the_cache()
	{
		const string source = """
			type Counter {
				#i32 calls = 0
				#fun bump() -> i32 {
					calls += 1
					calls
				}
			}

			#[memo] fun double(i32 n) -> i32 {
				Counter.bump()
				n * 2
			}

			fun runs() -> i32 {
				double(3)
				double(3)
				double(4)
				double(4)
				Counter.calls
			}
			""";

		Assert.Equal(KValue.I32(2), StdPipeline.Run(source, "runs"));
	}

	[Fact]
	public void Different_arguments_are_cached_separately()
	{
		const string source = """
			#[memo] fun add(i32 a, i32 b) -> i32 a + b
			fun value() -> i32 add(2, 3) + add(2, 3) + add(1, 1)
			""";

		Assert.Equal(KValue.I32(12), StdPipeline.Run(source, "value"));
	}

	[Fact]
	public void Memo_function_returns_correct_values()
	{
		const string source = """
			#[memo] fun fib(i32 n) -> i32 {
				if n < 2 { n } else { fib(n - 1) + fib(n - 2) }
			}
			fun value() -> i32 fib(20)
			""";

		Assert.Equal(KValue.I32(6765), StdPipeline.Run(source, "value"));
	}
}
