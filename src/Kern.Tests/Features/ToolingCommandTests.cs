using System.Text;
using Kern.Cli;
using Kern.Cli.Commands;
using Kern.Cli.Infrastructure;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Инструменты CLI: <c>kernc new</c>, <c>kernc doc</c> (doctest) и снимок файлов
/// для <c>kernc run --watch</c>.
/// </summary>
[Collection("timing")]
public sealed class ToolingCommandTests
{
	[Fact]
	public void New_app_creates_a_runnable_project()
	{
		string directory = Temp("kern-new-app");
		try
		{
			int code = Run(["new", "app", directory], out string output, out string error);
			Assert.Equal(0, code);
			Assert.Contains("создан проект app", output);
			Assert.True(File.Exists(Path.Combine(directory, "build.kern")));
			Assert.True(File.Exists(Path.Combine(directory, "src", "main.kern")));

			int check = Run(["check", directory], out _, out string checkError);
			Assert.True(check == 0, checkError);
		}
		finally
		{
			Delete(directory);
		}
	}

	[Fact]
	public void New_lib_checks_even_without_an_app_target()
	{
		string directory = Temp("kern-new-lib");
		try
		{
			Assert.Equal(0, Run(["new", "lib", directory], out _, out _));
			Assert.True(File.Exists(Path.Combine(directory, "src", "lib.kern")));
			int check = Run(["check", directory], out _, out string error);
			Assert.True(check == 0, error);
		}
		finally
		{
			Delete(directory);
		}
	}

	[Fact]
	public void New_library_alias_creates_the_same_project()
	{
		string directory = Temp("kern-new-classlib");
		try
		{
			int code = Run(["new", "classlib", directory], out string output, out string error);
			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error);
			Assert.Contains("создан проект lib", output);
			Assert.True(File.Exists(Path.Combine(directory, "src", "lib.kern")));
			Assert.Contains("t.kind = \"lib\"", File.ReadAllText(Path.Combine(directory, "build.kern")));
		}
		finally
		{
			Delete(directory);
		}
	}

	[Fact]
	public void New_console_alias_creates_an_app_project()
	{
		string directory = Temp("kern-new-console");
		try
		{
			Assert.Equal(0, Run(["new", "console", directory], out string output, out _));
			Assert.Contains("создан проект app", output);
			Assert.True(File.Exists(Path.Combine(directory, "src", "main.kern")));
			Assert.Contains("t.kind = \"app\"", File.ReadAllText(Path.Combine(directory, "build.kern")));
		}
		finally
		{
			Delete(directory);
		}
	}

	[Fact]
	public void Explain_type_prints_inferred_type_and_generic_arguments()
	{
		string directory = Temp("kern-explain");
		try
		{
			Directory.CreateDirectory(directory);
			string file = Path.Combine(directory, "main.kern");
			File.WriteAllText(file, """
				fun id<T>(T x) -> T x
				fun main { }
				""", new UTF8Encoding(false));

			int code = Run(["explain-type", file, "id(5)"], out string output, out string error);

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error);
			Assert.Contains("тип: i32", output);
			Assert.Contains("generic: T=i32", output);
		}
		finally
		{
			Delete(directory);
		}
	}

	[Fact]
	public void Run_profile_prints_functions_and_hot_lines()
	{
		string directory = Temp("kern-profile");
		try
		{
			Directory.CreateDirectory(directory);
			string file = Path.Combine(directory, "main.kern");
			File.WriteAllText(file, """
				fun work(i32 n) -> i32 {
					i32 total = 0
					loop i in 0..n { total += i }
					total
				}

				fun main { work(10) }
				""", new UTF8Encoding(false));

			int code = Run(["run", "--profile", file], out string output, out string error);

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error);
			Assert.Contains("; профиль: функции", output);
			Assert.Contains("work", output);
			Assert.Contains("; горячие строки", output);
			Assert.Contains("main.kern:", output);
		}
		finally
		{
			Delete(directory);
		}
	}

	[Fact]
	public void New_refuses_a_non_empty_directory()
	{
		string directory = Temp("kern-new-busy");
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "keep.txt"), "x");
		try
		{
			int code = Run(["new", "app", directory], out _, out string error);
			Assert.Equal(1, code);
			Assert.Contains("не пуста", error);
			Assert.True(File.Exists(Path.Combine(directory, "keep.txt")));
		}
		finally
		{
			Delete(directory);
		}
	}

	[Fact]
	public void Doc_generates_markdown_and_runs_a_passing_doctest()
	{
		string directory = Temp("kern-doc");
		Directory.CreateDirectory(directory);
		string source = Path.Combine(directory, "sample.kern");
		string output = Path.Combine(directory, "doc");
		File.WriteAllText(source, """
			/// Складывает два числа.
			///
			/// ```kern
			/// fun main {
			///     println(2 + 2)
			/// }
			/// // expect: 4
			/// ```
			fun add(i32 a, i32 b) -> i32 a + b
			""");
		try
		{
			int code = DocCommand.Run([source, "-o", output], new StringWriter(), new StringWriter());
			Assert.Equal(0, code);
			string markdown = File.ReadAllText(Path.Combine(output, "sample.md"));
			Assert.Contains("Складывает два числа", markdown);
			Assert.Contains("fun add", markdown);
			Assert.Contains("```kern", markdown);
		}
		finally
		{
			Delete(directory);
		}
	}

	[Fact]
	public void Doc_fails_when_a_doctest_expectation_differs()
	{
		string directory = Temp("kern-doc-bad");
		Directory.CreateDirectory(directory);
		string source = Path.Combine(directory, "sample.kern");
		File.WriteAllText(source, """
			/// ```kern
			/// fun main {
			///     println(2 + 2)
			/// }
			/// // expect: 5
			/// ```
			fun add(i32 a, i32 b) -> i32 a + b
			""");
		var error = new StringWriter();
		try
		{
			int code = DocCommand.Run([source, "-o", Path.Combine(directory, "doc")], new StringWriter(), error);
			Assert.Equal(1, code);
			Assert.Contains("ожидалось", error.ToString());
		}
		finally
		{
			Delete(directory);
		}
	}

	[Fact]
	public void Watch_snapshot_detects_changed_sources()
	{
		string directory = Temp("kern-watch");
		Directory.CreateDirectory(Path.Combine(directory, "src"));
		string file = Path.Combine(directory, "src", "main.kern");
		File.WriteAllText(file, "fun main { println(1) }");
		try
		{
			WatchSet first = WatchCommand.Snapshot(directory, SourceKind.Project);
			Assert.Equal(1, first.Count);
			Thread.Sleep(20);
			File.WriteAllText(file, "fun main { println(2) }");
			WatchSet second = WatchCommand.Snapshot(directory, SourceKind.Project);
			Assert.True(second.Differs(first));
			Assert.False(second.Differs(WatchCommand.Snapshot(directory, SourceKind.Project)));
		}
		finally
		{
			Delete(directory);
		}
	}

	private static int Run(string[] args, out string stdout, out string stderr)
	{
		var output = new StringWriter();
		var errors = new StringWriter();
		int code = KernCli.Run(args, output, errors);
		stdout = output.ToString();
		stderr = errors.ToString();
		return code;
	}

	private static string Temp(string prefix) =>
		Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));

	private static void Delete(string directory)
	{
		if (Directory.Exists(directory))
			Directory.Delete(directory, recursive: true);
	}
}
