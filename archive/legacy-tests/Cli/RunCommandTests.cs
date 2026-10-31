using Kern.Cli;
using Xunit;

namespace Kern.Tests.Cli;

/// <summary>
/// <c>kernc run</c>: команда вызывается в процессе с перехваченными потоками. Вывод самой
/// программы идёт через <see cref="Console"/>, поэтому тест подменяет <see cref="Console.Out"/>.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public sealed class RunCommandTests
{
	[Fact]
	public void Run_executes_main_and_prints_output()
	{
		string path = WriteSource("""
			fun main {
				println("привет из Kern")
			}
			""");

		TextWriter original = Console.Out;
		using StringWriter programOutput = new();
		try
		{
			Console.SetOut(programOutput);
			(int code, string output, string error) = Run("run", path);

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error);
			Assert.Equal(string.Empty, output);
			Assert.Contains("привет из Kern", programOutput.ToString(), StringComparison.Ordinal);
		}
		finally
		{
			Console.SetOut(original);
			File.Delete(path);
		}
	}

	/// <summary>
	/// Требование «возвращаемое значение main игнорируется» сейчас недостижимо: binder запрещает
	/// main с возвращаемым типом (KRN2107), см. docs/KNOWN_ISSUES.md §E. CLI не меняет это правило
	/// и всегда игнорирует то, что вернул <c>Interpreter.Run("main")</c>.
	/// </summary>
	[Fact]
	public void Run_rejects_main_with_return_value()
	{
		string path = WriteSource("fun main() -> i32 { 1 }");

		try
		{
			(int code, string output, string error) = Run("run", path);

			Assert.Equal(1, code);
			Assert.Equal(string.Empty, output);
			Assert.Contains("KRN2107", error, StringComparison.Ordinal);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Run_reports_compile_error_with_location()
	{
		string path = WriteSource("""
			fun main {
				i32 x = "текст"
			}
			""");

		try
		{
			(int code, string output, string error) = Run("run", path);

			Assert.Equal(1, code);
			Assert.Equal(string.Empty, output);
			Assert.Contains("ошибка KRN2102", error, StringComparison.Ordinal);
			Assert.Contains($"{path}:2:", error, StringComparison.Ordinal);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Run_without_main_reports_error()
	{
		string path = WriteSource("fun add(i32 a, i32 b) -> i32 { a + b }");

		try
		{
			(int code, _, string error) = Run("run", path);

			Assert.Equal(1, code);
			Assert.Contains("в файле нет функции main", error, StringComparison.Ordinal);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Run_reports_runtime_panic_without_stack_trace()
	{
		string path = WriteSource("""
			fun main {
				panic("boom")
			}
			""");

		try
		{
			(int code, _, string error) = Run("run", path);

			Assert.Equal(1, code);
			Assert.Contains("ошибка выполнения", error, StringComparison.Ordinal);
			Assert.Contains("boom", error, StringComparison.Ordinal);
			Assert.DoesNotContain("   at ", error, StringComparison.Ordinal);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Run_without_path_reports_usage()
	{
		(int code, _, string error) = Run("run");

		Assert.Equal(2, code);
		Assert.Contains("kernc run <файл.kern>", error, StringComparison.Ordinal);
	}

	[Fact]
	public void Run_passes_trailing_arguments_to_main_as_string_list()
	{
		string path = WriteSource("""
			fun main(list<str> args) {
				println(args.len)
			}
			""");

		try
		{
			(int code, string output, string error, string programOutput) =
				RunWithProgramOutput("run", path, "a", "b", "c");

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, output);
			Assert.Equal(string.Empty, error);
			Assert.Equal("3\n", Normalize(programOutput));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Run_arguments_arrive_in_order()
	{
		string path = WriteSource("""
			fun main(list<str> args) {
				println(args[0])
				println(args[1])
			}
			""");

		try
		{
			(int code, _, string error, string programOutput) =
				RunWithProgramOutput("run", path, "первый", "второй");

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error);
			Assert.Equal("первый\nвторой\n", Normalize(programOutput));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Run_rejects_main_with_str_parameter()
	{
		string path = WriteSource("fun main(str arg) { println(arg) }");

		try
		{
			(int code, string output, string error) = Run("run", path);

			Assert.Equal(1, code);
			Assert.Equal(string.Empty, output);
			Assert.Contains("KRN2107", error, StringComparison.Ordinal);
			Assert.Contains("list<str>", error, StringComparison.Ordinal);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Run_without_parameters_ignores_trailing_arguments()
	{
		string path = WriteSource("""
			fun main {
				println("ok")
			}
			""");

		try
		{
			(int code, _, string error, string programOutput) =
				RunWithProgramOutput("run", path, "лишний", "аргумент");

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error);
			Assert.Equal("ok\n", Normalize(programOutput));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Run_rejects_unknown_flag()
	{
		(int code, _, string error) = Run("run", "--verbose");

		Assert.Equal(2, code);
		Assert.Contains("неизвестный параметр", error, StringComparison.Ordinal);
	}

	[Fact]
	public void Run_rejects_non_kern_extension()
	{
		(int code, _, string error) = Run("run", Path.Combine(Path.GetTempPath(), "program.txt"));

		Assert.Equal(2, code);
		Assert.Contains("принимает файл .kern", error, StringComparison.Ordinal);
	}

	[Fact]
	public void Run_missing_file_reports_name()
	{
		string path = Path.Combine(Path.GetTempPath(), $"kern-cli-{Guid.NewGuid():N}.kern");

		(int code, _, string error) = Run("run", path);

		Assert.Equal(2, code);
		Assert.Contains("файл не найден", error, StringComparison.Ordinal);
		Assert.Contains(path, error, StringComparison.Ordinal);
	}

	[Fact]
	public void Help_run_describes_command()
	{
		(int code, string output, string error) = Run("help", "run");

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, error);
		Assert.Contains("kernc run <файл.kern>", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Help_build_describes_command()
	{
		(int code, string output, string error) = Run("help", "build");

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, error);
		Assert.Contains("kernc build <файл.kern>", output, StringComparison.Ordinal);
	}

	[Fact]
	public void General_help_lists_run_and_build()
	{
		(int code, string output, string error) = Run("help");

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, error);
		Assert.Contains("run <файл.kern>", output, StringComparison.Ordinal);
		Assert.Contains("build <файл.kern>", output, StringComparison.Ordinal);
	}

	private static (int Code, string Output, string Error) Run(params string[] args)
	{
		using StringWriter stdout = new();
		using StringWriter stderr = new();
		int code = KernCli.Run(args, stdout, stderr);
		return (code, stdout.ToString(), stderr.ToString());
	}

	/// <summary>Как <see cref="Run"/>, но дополнительно перехватывает вывод программы через <see cref="Console"/>.</summary>
	private static (int Code, string Output, string Error, string ProgramOutput) RunWithProgramOutput(
		params string[] args)
	{
		TextWriter original = Console.Out;
		using StringWriter captured = new();
		try
		{
			Console.SetOut(captured);
			(int code, string output, string error) = Run(args);
			return (code, output, error, captured.ToString());
		}
		finally
		{
			Console.SetOut(original);
		}
	}

	private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

	private static string WriteSource(string source)
	{
		string directory = Path.Combine(Path.GetTempPath(), "kern-cli-run-tests");
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, $"run-{Guid.NewGuid():N}.kern");
		File.WriteAllText(path, source);
		return path;
	}
}
