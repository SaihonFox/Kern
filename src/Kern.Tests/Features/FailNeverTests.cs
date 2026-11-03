using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// <c>fail</c> имеет тип <c>never</c> (spec §7): вызов компилируется в любой позиции,
/// включая ветвь выражения, и паникует при выполнении.
/// </summary>
public sealed class FailNeverTests
{
	[Fact]
	public void Fail_is_allowed_where_a_value_is_expected()
	{
		const string source = """
			fun always() -> i32 fail("boom")

			fun branch(bool flag) -> i32 {
				if flag { 1 } else { fail("boom") }
			}

			fun arm(i32 n) -> str {
				match n {
					0 => "zero"
					_ => fail("boom")
				}
			}
			""";

		StdPipeline.Compile(source);
		Assert.Equal(KValue.I32(1), StdPipeline.Run(source + "\nfun ok() -> i32 branch(true)", "ok"));
		Assert.Equal(KValue.Str("zero"), StdPipeline.Run(source + "\nfun zero() -> str arm(0)", "zero"));
	}

	[Fact]
	public void Fail_panics_with_its_message()
	{
		const string source = """
			fun always() -> i32 fail("boom")
			fun hit() -> i32 always()
			""";

		KernPanic panic = StdPipeline.RunPanics(source, "hit");
		Assert.Contains("boom", panic.Message);
	}
}
