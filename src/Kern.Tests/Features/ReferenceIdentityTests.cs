using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>spec §3: <c>===</c>/<c>!==</c> — сравнение ссылок значений <c>type</c>.</summary>
public sealed class ReferenceIdentityTests
{
	[Fact]
	public void Reference_identity_is_not_value_equality()
	{
		const string source = """
			type Point(i32 x)

			fun same() -> bool {
				var point = Point(1)
				point === point
			}

			fun different() -> bool {
				Point(1) === Point(1)
			}

			fun different_not_equal() -> bool {
				Point(1) !== Point(1)
			}
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "same"));
		Assert.Equal(KValue.Bool(false), StdPipeline.Run(source, "different"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "different_not_equal"));
	}
}
