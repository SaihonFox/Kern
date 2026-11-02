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
		string marker = Path.Combine(directory, "marker.txt");
		System.IO.File.WriteAllText(marker, "ok");
		string file = Path.Combine(directory, "main.kern");
		System.IO.File.WriteAllText(
			file,
			"""
			use kern.env[env]
			use kern.file[file]

			fun main {
				println(env.cwd)
				println(file.exists("marker.txt"))
			}
			""");
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

			string[] lines = stdout.ToString().Trim().Split('\n', StringSplitOptions.TrimEntries);
			Assert.Equal(0, code);
			Assert.True(lines.Length >= 2, stdout.ToString());
			// Сравниваем не строку пути (на macOS /var — символическая ссылка на
			// /private/var), а факт: cwd — абсолютный путь с тем же именем каталога
			// и с marker.txt рядом. Это не зависит от реального пути в ОС.
			string workingDirectory = lines[0];
			Assert.True(Path.IsPathRooted(workingDirectory), workingDirectory);
			Assert.Equal(Path.GetFileName(directory), Path.GetFileName(workingDirectory));
			Assert.Equal("true", lines[1]);
			Assert.True(System.IO.File.Exists(Path.Combine(workingDirectory, "marker.txt")));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}
}
