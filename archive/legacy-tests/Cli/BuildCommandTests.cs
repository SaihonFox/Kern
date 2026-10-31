using Kern.Bytecode;
using Kern.Cli;
using Xunit;

namespace Kern.Tests.Cli;

/// <summary>
/// <c>kernc build</c>: пишет .kbc рядом с исходником или по <c>-o</c>; файл без <c>main</c>
/// тоже собирается. Все временные файлы удаляются в <c>finally</c>.
/// </summary>
public sealed class BuildCommandTests
{
	[Fact]
	public void Build_writes_kbc_next_to_source_by_default()
	{
		string source = WriteSource("fun add(i32 a, i32 b) -> i32 { a + b }");
		string expected = Path.ChangeExtension(source, ".kbc");

		try
		{
			(int code, string output, string error) = Run("build", source);

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error);
			Assert.Equal(expected + Environment.NewLine, output);
			Assert.True(File.Exists(expected));

			KbcModule module = KbcReader.Read(File.ReadAllBytes(expected));
			Assert.Equal(Path.GetFileNameWithoutExtension(source), module.Name);
		}
		finally
		{
			File.Delete(source);
			File.Delete(expected);
		}
	}

	[Fact]
	public void Build_honours_output_path()
	{
		string source = WriteSource("fun main { println(\"привет\") }");
		string target = Path.Combine(Path.GetDirectoryName(source)!, $"{Guid.NewGuid():N}.kbc");

		try
		{
			(int code, string output, string error) = Run("build", source, "-o", target);

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error);
			Assert.Equal(target + Environment.NewLine, output);
			Assert.True(File.Exists(target));

			KbcModule module = KbcReader.Read(File.ReadAllBytes(target));
			Assert.True(module.TryFindFunction("main", 0, out _));
		}
		finally
		{
			File.Delete(source);
			File.Delete(target);
		}
	}

	[Fact]
	public void Build_compile_error_writes_nothing()
	{
		string source = WriteSource("""
			fun main {
				i32 x = "текст"
			}
			""");
		string target = Path.Combine(Path.GetDirectoryName(source)!, $"{Guid.NewGuid():N}.kbc");

		try
		{
			(int code, _, string error) = Run("build", source, "-o", target);

			Assert.Equal(1, code);
			Assert.Contains("ошибка KRN2102", error, StringComparison.Ordinal);
			Assert.Contains($"{source}:2:", error, StringComparison.Ordinal);
			Assert.False(File.Exists(target));
		}
		finally
		{
			File.Delete(source);
			File.Delete(target);
		}
	}

	[Fact]
	public void Build_without_path_reports_usage()
	{
		(int code, _, string error) = Run("build");

		Assert.Equal(2, code);
		Assert.Contains("kernc build <файл.kern> [-o <файл.kbc>]", error, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_output_flag_without_path_reports_error()
	{
		string source = WriteSource("fun main { }");

		try
		{
			(int code, _, string error) = Run("build", source, "-o");

			Assert.Equal(2, code);
			Assert.Contains("параметр -o требует путь", error, StringComparison.Ordinal);
		}
		finally
		{
			File.Delete(source);
		}
	}

	[Fact]
	public void Build_rejects_several_sources()
	{
		(int code, _, string error) = Run("build", "one.kern", "two.kern");

		Assert.Equal(2, code);
		Assert.Contains("один файл .kern", error, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_rejects_unknown_flag()
	{
		(int code, _, string error) = Run("build", "--release", "app.kern");

		Assert.Equal(2, code);
		Assert.Contains("неизвестный параметр", error, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_missing_file_reports_name()
	{
		string path = Path.Combine(Path.GetTempPath(), $"kern-cli-{Guid.NewGuid():N}.kern");

		(int code, _, string error) = Run("build", path);

		Assert.Equal(2, code);
		Assert.Contains("файл не найден", error, StringComparison.Ordinal);
	}

	private static (int Code, string Output, string Error) Run(params string[] args)
	{
		using StringWriter stdout = new();
		using StringWriter stderr = new();
		int code = KernCli.Run(args, stdout, stderr);
		return (code, stdout.ToString(), stderr.ToString());
	}

	private static string WriteSource(string source)
	{
		string directory = Path.Combine(Path.GetTempPath(), "kern-cli-build-tests");
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, $"build-{Guid.NewGuid():N}.kern");
		File.WriteAllText(path, source);
		return path;
	}
}
