using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Открытые границы среза (spec §3): <c>xs[..b]</c> начинается с нуля, <c>xs[a..]</c>
/// заканчивается длиной коллекции, <c>xs[..]</c> — копия целиком; работает и для <c>str</c>.
/// </summary>
public sealed class OpenSliceTests
{
	[Fact]
	public void Open_start_and_open_end_slices_on_lists()
	{
		const string source = """
			fun head() -> str [1, 2, 3, 4, 5][..3].join(",")
			fun tail() -> str [1, 2, 3, 4, 5][1..].join(",")
			fun whole() -> str [1, 2, 3, 4, 5][..].join(",")
			""";

		Assert.Equal(KValue.Str("1,2,3"), StdPipeline.Run(source, "head"));
		Assert.Equal(KValue.Str("2,3,4,5"), StdPipeline.Run(source, "tail"));
		Assert.Equal(KValue.Str("1,2,3,4,5"), StdPipeline.Run(source, "whole"));
	}

	[Fact]
	public void Open_start_slice_keeps_the_step()
	{
		const string source = """
			fun stepped() -> str [0, 1, 2, 3, 4, 5, 6, 7][..8:2].join(",")
			fun tail_stepped() -> str [0, 1, 2, 3, 4, 5, 6, 7][1..:2].join(",")
			""";

		Assert.Equal(KValue.Str("0,2,4,6"), StdPipeline.Run(source, "stepped"));
		Assert.Equal(KValue.Str("1,3,5,7"), StdPipeline.Run(source, "tail_stepped"));
	}

	[Fact]
	public void Open_slices_on_strings()
	{
		const string source = """
			fun head() -> str "abcdef"[..3]
			fun tail() -> str "abcdef"[2..]
			fun whole() -> str "abcdef"[..]
			""";

		Assert.Equal(KValue.Str("abc"), StdPipeline.Run(source, "head"));
		Assert.Equal(KValue.Str("cdef"), StdPipeline.Run(source, "tail"));
		Assert.Equal(KValue.Str("abcdef"), StdPipeline.Run(source, "whole"));
	}

	[Fact]
	public void Open_start_slice_of_empty_collection_is_empty()
	{
		const string source = """
			fun list_empty() -> i32 {
				list<i32> empty = []
				empty[..].len
			}

			fun string_empty() -> i32 "abc"[3..].len
			""";

		Assert.Equal(KValue.I32(0), StdPipeline.Run(source, "list_empty"));
		Assert.Equal(KValue.I32(0), StdPipeline.Run(source, "string_empty"));
	}
}
