using Kern.Core.Text;
using Xunit;

namespace Kern.Tests.Text;

/// <summary>
/// Мультифайловый модуль: склейка текстов, карта «смещение -> файл:строка:колонка»,
/// границы файлов, пустой файл, "\n"/"\r\n"/"\r" и зажим смещений.
/// </summary>
public sealed class SourceBundleTests
{
	// Склейка: "a\nb" + "\n" + "c\rd" + "\n" + "e" = "a\nb\nc\rd\ne".
	private const string FirstText = "a\nb";
	private const string SecondText = "c\rd";
	private const string ThirdText = "e";
	private const string Joined = "a\nb\nc\rd\ne";

	[Fact]
	public void Create_joins_files_with_a_newline_and_keeps_paths()
	{
		var bundle = CreateSimple();

		Assert.Equal(Joined, bundle.Text.Text);
		Assert.Equal("first.kern", bundle.Text.Path);
		Assert.Equal(new[] { "first.kern", "second.kern", "third.kern" }, bundle.Paths);
		Assert.Equal(3, bundle.FileCount);
	}

	[Fact]
	public void Offsets_map_to_files_and_file_local_lines()
	{
		var bundle = CreateSimple();

		Assert.Equal(("first.kern", 1, 1), bundle.GetLineColumn(0));
		Assert.Equal(("first.kern", 2, 1), bundle.GetLineColumn(2));
		Assert.Equal(("second.kern", 1, 1), bundle.GetLineColumn(4));
		Assert.Equal(("second.kern", 2, 1), bundle.GetLineColumn(6));
		Assert.Equal(("third.kern", 1, 1), bundle.GetLineColumn(8));
	}

	[Fact]
	public void Offset_on_a_separator_belongs_to_the_preceding_file()
	{
		var bundle = CreateSimple();

		// Смещение 3 — это "\n"-разделитель: в текст второго файла (начало 4) оно не входит.
		Assert.Equal("first.kern", bundle.PathAt(3));
		Assert.Equal(("first.kern", 2, 2), bundle.GetLineColumn(3));

		// Смещение 7 — разделитель перед третьим файлом.
		Assert.Equal("second.kern", bundle.PathAt(7));
		Assert.Equal(("second.kern", 2, 2), bundle.GetLineColumn(7));
	}

	[Fact]
	public void First_offset_of_a_file_is_already_inside_it()
	{
		var bundle = CreateSimple();

		Assert.Equal("second.kern", bundle.PathAt(4));
		Assert.Equal("third.kern", bundle.PathAt(8));
		Assert.Equal("third.kern", bundle.PathAt(bundle.Text.Length));
	}

	[Fact]
	public void Line_offset_converts_bundle_lines_to_file_local_ones()
	{
		var bundle = CreateSimple();

		// Второй файл начинается с третьей строки общего текста.
		Assert.Equal(3, bundle.Text.GetLine(4));
		Assert.Equal(2, bundle.LineOffset(4));
		Assert.Equal(1, bundle.GetLocalLine(4));
		Assert.Equal(bundle.Text.GetLine(6) - bundle.LineOffset(6), bundle.GetLocalLine(6));
	}

	[Fact]
	public void Trailing_newline_does_not_shift_the_next_file()
	{
		var bundle = SourceBundle.Create([("a.kern", "x\ny\n"), ("b.kern", "z")]);
		var secondStart = bundle.Text.Text.IndexOf('z');

		Assert.Equal(4, bundle.Text.GetLine(secondStart));
		Assert.Equal("b.kern", bundle.PathAt(secondStart));
		Assert.Equal(("b.kern", 1, 1), bundle.GetLineColumn(secondStart));
	}

	[Fact]
	public void Empty_file_is_legal_and_owns_its_start()
	{
		const string first = "x\ny\n";
		var bundle = SourceBundle.Create([("a.kern", first), ("empty.kern", ""), ("b.kern", "z")]);
		var emptyStart = first.Length + 1;

		Assert.Equal("empty.kern", bundle.PathAt(emptyStart));
		Assert.Equal(("empty.kern", 1, 1), bundle.GetLineColumn(emptyStart));
		Assert.Equal(("b.kern", 1, 1), bundle.GetLineColumn(emptyStart + 1));

		var onlyEmpty = SourceBundle.Create([("only.kern", "")]);
		Assert.Equal("", onlyEmpty.Text.Text);
		Assert.Equal(("only.kern", 1, 1), onlyEmpty.GetLineColumn(0));
	}

	[Fact]
	public void Crlf_and_lf_files_keep_their_own_line_numbers()
	{
		const string first = "fun a() -> i32 {\r\n\tret 1\r\n}\r\n";
		const string second = "fun b() -> i32 {\n\tret 2\n}\n";
		var bundle = SourceBundle.Create([("a.kern", first), ("b.kern", second)]);
		var firstReturn = first.IndexOf("ret", StringComparison.Ordinal);
		var secondReturn = first.Length + 1 + second.IndexOf("ret", StringComparison.Ordinal);

		Assert.Equal(("a.kern", 2, 2), bundle.GetLineColumn(firstReturn));
		Assert.Equal(("b.kern", 2, 2), bundle.GetLineColumn(secondReturn));
	}

	[Fact]
	public void Offsets_outside_the_bundle_are_clamped()
	{
		var bundle = CreateSimple();

		Assert.Equal(("first.kern", 1, 1), bundle.GetLineColumn(-10));
		Assert.Equal("first.kern", bundle.PathAt(-1));
		Assert.Equal(("third.kern", 1, 2), bundle.GetLineColumn(1000));
		Assert.Equal(1, bundle.GetLocalLine(1000));
	}

	[Fact]
	public void Create_rejects_an_empty_file_list()
	{
		Assert.Throws<ArgumentException>(() => SourceBundle.Create([]));
	}

	private static SourceBundle CreateSimple() => SourceBundle.Create(
	[
		("first.kern", FirstText),
		("second.kern", SecondText),
		("third.kern", ThirdText)
	]);
}
