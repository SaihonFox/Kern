using Kern.Core.Diagnostics;
using Kern.Core.Text;
using Xunit;

namespace Kern.Tests.Core;

/// <summary>M4: карта строк исходника — "\n", "\r\n", одиночный "\r" и границы смещений.</summary>
public sealed class SourceTextTests
{
	[Fact]
	public void Empty_text_has_a_single_line()
	{
		var text = new SourceText(string.Empty);

		Assert.Equal(1, text.LineCount);
		Assert.Equal(1, text.GetLine(0));
		Assert.Equal((1, 1), text.GetLineColumn(0));
	}

	[Fact]
	public void Line_breaks_cover_all_three_newline_kinds()
	{
		// a \n b \r\n c \r d
		var text = new SourceText("a\nb\r\nc\rd");

		Assert.Equal(4, text.LineCount);
		Assert.Equal(1, text.GetLine(0));
		Assert.Equal(2, text.GetLine(2));
		Assert.Equal(3, text.GetLine(5));
		Assert.Equal(4, text.GetLine(7));
	}

	[Fact]
	public void Unicode_line_separators_do_not_start_a_line()
	{
		var text = new SourceText("a\u2028b\u2029c");

		Assert.Equal(1, text.LineCount);
		Assert.Equal(1, text.GetLine(2));
	}

	[Fact]
	public void Columns_are_one_based_and_respect_crlf()
	{
		var text = new SourceText("ab\r\ncd");

		Assert.Equal((1, 1), text.GetLineColumn(0));
		Assert.Equal((1, 3), text.GetLineColumn(2));
		Assert.Equal((2, 1), text.GetLineColumn(4));
		Assert.Equal((2, 2), text.GetLineColumn(5));
	}

	[Fact]
	public void Offset_at_the_end_of_text_uses_the_last_line()
	{
		Assert.Equal((1, 4), new SourceText("abc").GetLineColumn(3));
		Assert.Equal((3, 1), new SourceText("a\nb\n").GetLineColumn(4));
	}

	[Fact]
	public void Offsets_outside_the_text_are_clamped()
	{
		var text = new SourceText("a\nb");

		Assert.Equal((1, 1), text.GetLineColumn(-10));
		Assert.Equal(2, text.GetLine(999));
	}

	[Fact]
	public void Line_span_excludes_the_line_break()
	{
		var text = new SourceText("a\r\nbc\n");

		Assert.Equal(new TextSpan(0, 1), text.LineSpan(1));
		Assert.Equal(new TextSpan(3, 2), text.LineSpan(2));
		Assert.Equal(new TextSpan(6, 0), text.LineSpan(3));
	}
}
