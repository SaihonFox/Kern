using Kern.Cli;
using Xunit;

namespace Kern.Tests.Examples;

/// <summary>
/// M12: примеры с маркером <c>.compile-only</c> (сеть/тесты/библиотека) обязаны проходить
/// <c>kernc check</c>, но не запускаются как golden-примеры.
/// </summary>
[Collection("timing")]
public sealed class ExampleCompileTests
{
	[Theory]
	[InlineData("16_mocks")]
	[InlineData("17_http_clients")]
	[InlineData("31_kbc/mathx")]
	public void Compile_only_example_checks(string example)
	{
		string directory = Path.Combine(RepositoryRoot(), "examples", example.Replace('/', Path.DirectorySeparatorChar));
		var stdout = new StringWriter();
		var stderr = new StringWriter();

		int code = KernCli.Run(["check", directory], stdout, stderr);

		Assert.True(code == 0, stderr.ToString());
		Assert.Equal(string.Empty, stderr.ToString());
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
