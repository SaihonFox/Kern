using Kern.Cli;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Self-hosted компилятор (<c>bootstrap/</c>): проект собирается, тесты лексера и
/// парсера проходят, а <c>run</c> печатает AST.
/// </summary>
[Collection("timing")]
public sealed class SelfHostTests
{
	[Fact]
	public void Selfhost_lexer_tests_and_demo_run()
	{
		string directory = Path.Combine(RepositoryRoot(), "bootstrap");
		var stdout = new StringWriter();
		var stderr = new StringWriter();

		int testCode = KernCli.Run(["test", directory], stdout, stderr);
		Assert.True(testCode == 0, stderr.ToString());
		Assert.Contains("тестов: 12, успешно: 12", stdout.ToString());

		stdout.GetStringBuilder().Clear();
		stderr.GetStringBuilder().Clear();
		TextWriter original = Console.Out;
		var programOutput = new StringWriter();
		int runCode;
		Console.SetOut(programOutput);
		try
		{
			runCode = KernCli.Run(["run", directory], stdout, stderr);
		}
		finally
		{
			Console.SetOut(original);
		}

		Assert.True(runCode == 0, stderr.ToString());
		Assert.Contains("fun main", programOutput.ToString());
	}

	private static string RepositoryRoot()
	{
		string? current = AppContext.BaseDirectory;
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current, "Kern.slnx")))
				return current;
			current = Path.GetDirectoryName(current);
		}

		throw new InvalidOperationException("не найден корень репозитория (Kern.slnx)");
	}
}
