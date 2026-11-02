using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Расширения <c>kern.text</c>: Левенштейн, нечёткий поиск, slug/транслитерация,
/// Unicode-графемы, Markdown и шаблон <c>{{key}}</c>.
/// </summary>
[Collection("timing")]
public sealed class TextExtrasTests
{
	[Fact]
	public void Levenshtein_and_fuzzy_search()
	{
		const string source = """
			fun distance() -> i32 levenshtein("кот", "код")
			fun fuzzy() -> bool fuzzy_contains("hello", "helo", 1)
			fun strict() -> bool fuzzy_contains("hello", "xyz", 1)
			""";

		Assert.Equal(KValue.I32(1), StdPipeline.Run(source, "distance"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "fuzzy"));
		Assert.Equal(KValue.Bool(false), StdPipeline.Run(source, "strict"));
	}

	[Fact]
	public void Slug_transliterates_cyrillic()
	{
		const string source = """
			fun value() -> str slug("Привет, мир!")
			""";

		Assert.Equal(KValue.Str("privet-mir"), StdPipeline.Run(source, "value"));
	}

	[Fact]
	public void Graphemes_keep_combining_marks_and_emoji_together()
	{
		const string source = """
			fun count() -> i32 graphemes("a🇷🇺e\u0301").len
			""";

		Assert.Equal(KValue.I32(3), StdPipeline.Run(source, "count"));
	}

	[Fact]
	public void Markdown_and_template_render_escape_html()
	{
		const string source = """
			fun html() -> str markdown_to_html("# Hi\n\n**bold**")
			fun template() -> str template_render("Привет, {{name}}!", ["name": "<Аня>"])
			""";

		Assert.Equal(KValue.Str("<h1>Hi</h1>\n<p><strong>bold</strong></p>\n"), StdPipeline.Run(source, "html"));
		Assert.Equal(KValue.Str("Привет, &lt;Аня&gt;!"), StdPipeline.Run(source, "template"));
	}

	[Fact]
	public void StringBuilder_accumulates_without_quadratic_concat()
	{
		const string source = """
			fun value() -> str {
				string_builder b()
				loop i in 0..4 {
					b.append(i)
					b.append_line("!")
				}
				b.to_str()
			}

			fun length() -> i32 {
				string_builder b()
				b.append("abc")
				b.append_line("de")
				b.len
			}

			fun reusable() -> str {
				string_builder b()
				b.append("old")
				b.clear()
				b.append("new")
				b.to_str()
			}
			""";

		Assert.Equal(KValue.Str("0!\n1!\n2!\n3!\n"), StdPipeline.Run(source, "value"));
		Assert.Equal(KValue.I32(6), StdPipeline.Run(source, "length"));
		Assert.Equal(KValue.Str("new"), StdPipeline.Run(source, "reusable"));
	}
}
