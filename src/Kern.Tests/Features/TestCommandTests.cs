using System.Text;
using Kern.Cli;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>CLI <c>kernc test</c>: запуск <c>#[test]</c>-функций файла и проекта.</summary>
[Collection("timing")]
public sealed class TestCommandTests : IDisposable
{
	private readonly List<string> _roots = [];

	[Fact]
	public void Test_command_runs_marked_functions()
	{
		string root = Create(
			("src/main.kern", """
				fun add(i32 a, i32 b) -> i32 a + b

				#[test]
				fun adds() { assert_eq(4, add(2, 2)) }

				fun main { }
				"""));

		var (code, stdout, stderr) = Run("test", root);

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, stderr);
		Assert.Contains("тест adds: ок", stdout, StringComparison.Ordinal);
		Assert.Contains("тестов: 1, успешно: 1, ошибок: 0", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public void Test_command_reports_a_failing_test()
	{
		string root = Create(
			("src/main.kern", """
				#[test]
				fun fails() { assert_eq(5, 4) }

				fun main { }
				"""));

		var (code, stdout, _) = Run("test", root);

		Assert.Equal(1, code);
		Assert.Contains("тест fails: ошибка:", stdout, StringComparison.Ordinal);
		Assert.Contains("ошибок: 1", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public void Test_command_runs_a_single_file()
	{
		string root = Create(
			("check.kern", """
				#[test]
				fun ok() { assert_eq(2, 1 + 1) }
				"""));

		var (code, stdout, stderr) = Run("test", Path.Combine(root, "check.kern"));

		Assert.Equal(0, code);
		Assert.Equal(string.Empty, stderr);
		Assert.Contains("тест ok: ок", stdout, StringComparison.Ordinal);
	}

	private string Create(params (string Path, string Text)[] files)
	{
		string root = Path.Combine(Path.GetTempPath(), "kern-test-command", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		_roots.Add(root);
		foreach (var (path, text) in files)
		{
			string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(full)!);
			File.WriteAllText(full, text, new UTF8Encoding(false));
		}

		return root;
	}

	private static (int Code, string Stdout, string Stderr) Run(params string[] args)
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		int code = KernCli.Run(args, stdout, stderr);
		return (code, stdout.ToString(), stderr.ToString());
	}

	public void Dispose()
	{
		foreach (string root in _roots)
		{
			try
			{
				if (Directory.Exists(root))
					Directory.Delete(root, recursive: true);
			}
			catch (IOException)
			{
			}
		}
	}
}
