using Kern.Cli;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>Запуск программы использует каталог исходника как текущий каталог.</summary>
public sealed class CliWorkingDirectoryTests
{
	[Fact]
	public void Run_uses_the_program_directory_as_cwd()
	{
		string directory = Path.Combine(Path.GetTempPath(), $"kern-cwd-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		string file = Path.Combine(directory, "main.kern");
		System.IO.File.WriteAllText(
			file,
			"use kern.env[env]\nfun main { println(env.cwd) }\n");
		try
		{
			var stdout = new StringWriter();
			TextWriter previous = Console.Out;
			Console.SetOut(stdout);
			int code;
			try
			{
				code = KernCli.Run(["run", file], stdout, TextWriter.Null);
			}
			finally
			{
				Console.SetOut(previous);
			}

			Assert.Equal(0, code);
			Assert.Equal(Path.GetFullPath(directory), stdout.ToString().Trim());
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}
}
