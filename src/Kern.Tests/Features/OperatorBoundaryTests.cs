using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>Границы операторов <c>!is</c>/<c>!in</c>: <c>!is_letter(x)</c> — это <c>!</c> и вызов.</summary>
public sealed class OperatorBoundaryTests
{
	[Fact]
	public void Negation_before_identifier_starting_with_is_is_not_is_operator()
	{
		const string source = """
			fun is_letter(char symbol) -> bool symbol.is_letter()
			fun check() -> bool !is_letter('1')
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "check"));
	}

	[Fact]
	public void Negated_membership_still_works()
	{
		const string source = """
			fun check() -> bool {
				list<i32> values = [1, 2]
				3 !in values
			}
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "check"));
	}
}
