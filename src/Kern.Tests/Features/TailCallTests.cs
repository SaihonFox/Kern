using Kern.Binding.Resolution;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Хвостовая саморекурсия (spec §6, раунд 27): прямой вызов текущей функции в позиции
/// возврата компилируется в <c>TailCall</c> и не наращивает стек VM; взаимная рекурсия
/// по-прежнему упирается в предел кадров.
/// </summary>
public sealed class TailCallTests
{
	private const string Source = """
		fun count(i32 n, i32 acc) -> i32 {
			if n == 0 { acc } else { count(n - 1, acc + 1) }
		}

		fun count_ret(i32 n) -> i32 {
			if n == 0 { ret 0 }
			ret count_ret(n - 1)
		}

		fun count_match(i32 n) -> i32 {
			match n {
				0 => 0
				_ => count_match(n - 1)
			}
		}

		fun even(i32 n) -> bool { if n == 0 { true } else { odd(n - 1) } }
		fun odd(i32 n) -> bool { if n == 0 { false } else { even(n - 1) } }
		""";

	[Fact]
	public void Self_recursion_in_tail_position_does_not_grow_the_stack()
	{
		Assert.Equal(
			KValue.I32(200_000),
			StdPipeline.Run(Source, "count", KValue.I32(200_000), KValue.I32(0)));
		Assert.Equal(KValue.I32(0), StdPipeline.Run(Source, "count_ret", KValue.I32(200_000)));
		Assert.Equal(KValue.I32(0), StdPipeline.Run(Source, "count_match", KValue.I32(200_000)));
	}

	[Fact]
	public void Mutual_recursion_is_not_optimized_and_reports_stack_overflow()
	{
		var interpreter = new Interpreter(
			StdPipeline.Compile(Source).Module,
			new NativeResolver(StdPipeline.Catalog()));

		KernPanic panic = Assert.Throws<KernPanic>(
			() => interpreter.Run("even", KValue.I32(200_000)));
		Assert.Contains("переполнение стека", panic.Message);
	}
}
