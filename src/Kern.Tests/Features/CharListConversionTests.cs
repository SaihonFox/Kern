using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Контекстные преобразования <c>str</c> ↔ <c>list&lt;char&gt;</c> (spec §3):
/// строка разворачивается в список символов и собирается обратно.
/// </summary>
public sealed class CharListConversionTests
{
	[Fact]
	public void String_initializes_a_char_list()
	{
		const string source = """
			fun length() -> i32 {
				list<char> chars = "hello"
				chars.len
			}

			fun element() -> bool {
				list<char> chars = "hello"
				chars[1] == 'e'
			}
			""";

		Assert.Equal(KValue.I32(5), StdPipeline.Run(source, "length"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "element"));
	}

	[Fact]
	public void Char_list_converts_back_to_string()
	{
		const string source = """
			fun roundtrip() -> str {
				list<char> chars = "hello"
				str text = chars
				text
			}

			fun empty() -> i32 {
				list<char> chars = ""
				str text = chars
				text.len
			}
			""";

		Assert.Equal(KValue.Str("hello"), StdPipeline.Run(source, "roundtrip"));
		Assert.Equal(KValue.I32(0), StdPipeline.Run(source, "empty"));
	}
}
