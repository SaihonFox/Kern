using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>Reified-аргументы нативных generic-контейнеров (variant 2): `KNativeBox.NativeType`.</summary>
public sealed class NativeGenericTests
{
	[Fact]
	public void Native_generic_handle_keeps_reified_type_arguments()
	{
		const string source = """
			fun matches() -> bool {
				set<i32> values = set<i32>()
				values.add(1)
				values is set<i32>
			}

			fun queue_matches() -> bool {
				queue<i32> values = queue<i32>()
				values is queue<i32>
			}
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "matches"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "queue_matches"));
	}
}
