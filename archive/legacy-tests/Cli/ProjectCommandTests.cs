using Kern.Bytecode;
using Kern.Cli;
using Xunit;

namespace Kern.Tests.Cli;

/// <summary>
/// Режим проекта (M8, docs/BUILDER.md §2): <c>kernc run|build|check|clean &lt;папка&gt;</c>.
/// Источники — <c>src/**/*.kern</c> или <c>*.kern</c> в корне, пути в диагностике и байткоде
/// относительны папке проекта. Все временные папки удаляются в <c>finally</c>.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public sealed class ProjectCommandTests
{
	[Fact]
	public void Run_directory_executes_main_and_calls_function_from_another_file()
	{
		string project = CreateProject(
			("src/main.kern", "fun main {\n\tprintln(combine())\n}\n"),
			("src/util.kern", "fun combine() -> i32 {\n\tadd(20, 22)\n}\n\nfun add(i32 a, i32 b) -> i32 {\n\ta + b\n}\n"));

		try
		{
			(int code, string output, string error, string programOutput) =
				RunWithProgramOutput("run", project);

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, output);
			Assert.Equal(string.Empty, error);
			Assert.Equal("42\n", Normalize(programOutput));
		}
		finally
		{
			DeleteProject(project);
		}
	}

	[Fact]
	public void Build_directory_writes_out_kbc_with_project_relative_source_files()
	{
		string project = CreateProject(
			("src/main.kern", "fun main {\n\tprintln(combine())\n}\n"),
			("src/util.kern", "fun combine() -> i32 {\n\tadd(20, 22)\n}\n\nfun add(i32 a, i32 b) -> i32 {\n\ta + b\n}\n"));
		string name = new DirectoryInfo(project).Name;
		string expected = Path.Combine(project, "out", name + ".kbc");

		try
		{
			(int code, string output, string error) = Run("build", project);

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error);
			Assert.Equal(expected + Environment.NewLine, output);
			Assert.True(File.Exists(expected));

			KbcModule module = KbcReader.Read(File.ReadAllBytes(expected));
			Assert.Equal(name, module.Name);

			var main = Assert.Single(module.FunctionPool, function => function.Name == "main");
			var combine = Assert.Single(module.FunctionPool, function => function.Name == "combine");
			Assert.Equal("src/main.kern", main.SourceFile);
			Assert.Equal("src/util.kern", combine.SourceFile);
		}
		finally
		{
			DeleteProject(project);
		}
	}

	[Fact]
	public void Build_directory_honours_output_path()
	{
		string project = CreateProject(
			("src/main.kern", "fun main {\n}\n"),
			("src/util.kern", "fun add(i32 a, i32 b) -> i32 {\n\ta + b\n}\n"));
		string target = Path.Combine(Path.GetTempPath(), $"kern-cli-{Guid.NewGuid():N}.kbc");

		try
		{
			(int code, string output, string error) = Run("build", project, "-o", target);

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error);
			Assert.Equal(target + Environment.NewLine, output);
			Assert.True(File.Exists(target));
			Assert.False(Directory.Exists(Path.Combine(project, "out")));

			KbcModule module = KbcReader.Read(File.ReadAllBytes(target));
			Assert.Equal(new DirectoryInfo(project).Name, module.Name);
		}
		finally
		{
			DeleteProject(project);
			File.Delete(target);
		}
	}

	[Fact]
	public void Build_compile_error_in_second_file_reports_relative_path()
	{
		string project = CreateProject(
			("src/main.kern", "fun main {\n}\n"),
			("src/util.kern", "fun broken() {\n\ti32 x = \"текст\"\n}\n"));

		try
		{
			(int code, string output, string error) = Run("build", project);

			Assert.Equal(1, code);
			Assert.Equal(string.Empty, output);
			Assert.Contains("src/util.kern:2:", error, StringComparison.Ordinal);
			Assert.Contains("ошибка KRN2102", error, StringComparison.Ordinal);
			Assert.False(Directory.Exists(Path.Combine(project, "out")));
		}
		finally
		{
			DeleteProject(project);
		}
	}

	[Fact]
	public void Run_project_without_src_uses_files_from_the_root()
	{
		string project = CreateProject(
			("main.kern", "fun main {\n\tprintln(greet())\n}\n"),
			("util.kern", "fun greet() -> str {\n\t\"без src\"\n}\n"));

		try
		{
			(int code, string output, string error, string programOutput) =
				RunWithProgramOutput("run", project);

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, output);
			Assert.Equal(string.Empty, error);
			Assert.Equal("без src\n", Normalize(programOutput));
		}
		finally
		{
			DeleteProject(project);
		}
	}

	[Fact]
	public void Check_directory_is_clean_and_writes_nothing()
	{
		string project = CreateProject(
			("src/main.kern", "fun main {\n}\n"),
			("src/util.kern", "fun add(i32 a, i32 b) -> i32 {\n\ta + b\n}\n"));

		try
		{
			(int code, string output, string error) = Run("check", project);

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, output);
			Assert.Equal(string.Empty, error);
			Assert.False(Directory.Exists(Path.Combine(project, "out")));
			Assert.False(Directory.Exists(Path.Combine(project, "dist")));
			Assert.Empty(Directory.EnumerateFiles(project, "*.kbc", SearchOption.AllDirectories));
		}
		finally
		{
			DeleteProject(project);
		}
	}

	[Fact]
	public void Check_bad_file_returns_one_and_prints_location()
	{
		string path = WriteSource("""
			fun main {
				i32 x = "текст"
			}
			""");

		try
		{
			(int code, string output, string error) = Run("check", path);

			Assert.Equal(1, code);
			Assert.Equal(string.Empty, output);
			Assert.Contains($"{path}:2:", error, StringComparison.Ordinal);
			Assert.Contains("ошибка KRN2102", error, StringComparison.Ordinal);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Check_good_file_returns_zero()
	{
		string path = WriteSource("fun add(i32 a, i32 b) -> i32 { a + b }");

		try
		{
			(int code, string output, string error) = Run("check", path);

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, output);
			Assert.Equal(string.Empty, error);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Clean_removes_out_and_dist()
	{
		string project = CreateProject(("src/main.kern", "fun main {\n}\n"));
		string outDirectory = Path.Combine(project, "out");
		string distDirectory = Path.Combine(project, "dist");
		Directory.CreateDirectory(outDirectory);
		Directory.CreateDirectory(distDirectory);
		File.WriteAllText(Path.Combine(outDirectory, "app.kbc"), "dummy");
		File.WriteAllText(Path.Combine(distDirectory, "app.exe"), "dummy");

		try
		{
			(int code, string output, string error) = Run("clean", project);

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error);
			Assert.Contains("удалено:", output, StringComparison.Ordinal);
			Assert.Contains(outDirectory, output, StringComparison.Ordinal);
			Assert.Contains(distDirectory, output, StringComparison.Ordinal);
			Assert.False(Directory.Exists(outDirectory));
			Assert.False(Directory.Exists(distDirectory));

			// Повторный clean при отсутствии каталогов — не ошибка.
			(int again, string secondOutput, string secondError) = Run("clean", project);
			Assert.Equal(0, again);
			Assert.Equal(string.Empty, secondError);
			Assert.Contains("нечего удалять", secondOutput, StringComparison.Ordinal);
		}
		finally
		{
			DeleteProject(project);
		}
	}

	[Fact]
	public void Clean_rejects_non_directory()
	{
		string path = WriteSource("fun main {\n}\n");

		try
		{
			(int code, _, string error) = Run("clean", path);

			Assert.Equal(2, code);
			Assert.Contains("папка не найдена", error, StringComparison.Ordinal);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Run_directory_without_main_reports_project_message()
	{
		string project = CreateProject(
			("src/main.kern", "fun add(i32 a, i32 b) -> i32 {\n\ta + b\n}\n"));

		try
		{
			(int code, _, string error) = Run("run", project);

			Assert.Equal(1, code);
			Assert.Contains("в проекте нет функции main", error, StringComparison.Ordinal);
		}
		finally
		{
			DeleteProject(project);
		}
	}

	[Fact]
	public void Run_missing_directory_reports_usage_error()
	{
		string path = Path.Combine(Path.GetTempPath(), $"kern-cli-missing-{Guid.NewGuid():N}");

		(int code, _, string error) = Run("run", path);

		Assert.Equal(2, code);
		Assert.Contains("принимает файл .kern или папку проекта", error, StringComparison.Ordinal);
		Assert.Contains(path, error, StringComparison.Ordinal);
	}

	[Fact]
	public void Help_lists_directory_mode_check_and_clean()
	{
		(int code, string output, string error) = Run("help");

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, error);
		Assert.Contains("check <файл.kern|папка>", output, StringComparison.Ordinal);
		Assert.Contains("clean <папка>", output, StringComparison.Ordinal);
		Assert.Contains("out/<имя>.kbc", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Help_check_describes_command()
	{
		(int code, string output, string error) = Run("help", "check");

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, error);
		Assert.Contains("kernc check <файл.kern|папка>", output, StringComparison.Ordinal);
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

	private static string CreateProject(params (string RelativePath, string Text)[] files)
	{
		string root = Path.Combine(
			Path.GetTempPath(), "kern-cli-project-tests", $"project-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		foreach (var (relativePath, text) in files)
		{
			string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, text);
		}

		return root;
	}

	private static void DeleteProject(string root)
	{
		try
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
		catch (IOException)
		{
			// Временная папка: не мешаем результату теста из-за чужой блокировки файла.
		}
		catch (UnauthorizedAccessException)
		{
			// См. выше.
		}
	}

	private static string WriteSource(string source)
	{
		string directory = Path.Combine(Path.GetTempPath(), "kern-cli-project-tests");
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, $"check-{Guid.NewGuid():N}.kern");
		File.WriteAllText(path, source);
		return path;
	}
}
