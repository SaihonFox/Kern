using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Identity для <c>unit</c> (spec §3/§9): <c>===</c>/<c>!==</c> сравнивают ссылку на
/// объект, а присваивание по-прежнему копирует значение; <c>==</c> остаётся структурным.
/// </summary>
public sealed class UnitIdentityTests
{
	private const string Source = """
		unit Point(i32 x)

		fun same() -> bool {
			Point p(1)
			p === p
		}

		fun copied() -> bool {
			Point p(1)
			Point q = p
			p === q
		}

		fun copied_not_same() -> bool {
			Point p(1)
			Point q = p
			p !== q
		}

		fun copied_value_equal() -> bool {
			Point p(1)
			Point q = p
			p == q
		}

		type Box(i32 x)

		fun class_same() -> bool {
			Box b(1)
			b === b
		}
		""";

	[Fact]
	public void Unit_identity_is_reference_identity_but_assignment_still_copies()
	{
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(Source, "same"));
		Assert.Equal(KValue.Bool(false), StdPipeline.Run(Source, "copied"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(Source, "copied_not_same"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(Source, "copied_value_equal"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(Source, "class_same"));
	}
}
