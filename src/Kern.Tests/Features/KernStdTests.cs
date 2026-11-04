using Kern.Cli;
using Kern.Std;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Пилот Kern.Std: исходники на Kern загружаются из embedded-ресурсов и компилируются
/// обычным проектом; <c>kern.ansi</c> на Kern доступен через <c>use</c>.
/// </summary>
[Collection("timing")]
public sealed class KernStdTests
{
	[Fact]
	public void Embedded_sources_include_ansi()
	{
		IReadOnlyList<(string Path, string Text)> files = StdSources.Load();
		Assert.Contains(files, file => file.Path.EndsWith("ansi.kern", StringComparison.Ordinal));
		Assert.Contains(files, file => file.Text.Contains("project kern.ansi:", StringComparison.Ordinal));
	}

	[Fact]
	public void Embedded_sources_compile_individually()
	{
		string directory = Path.Combine(Path.GetTempPath(), "kern-std-check-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			foreach (var (path, text) in StdSources.Load())
			{
				string file = Path.Combine(directory, Path.GetFileName(path));
				File.WriteAllText(file, text);
				var error = new StringWriter();
				int code = KernCli.Run(["check", file], TextWriter.Null, error);
				Assert.True(code == 0, $"{path}: {error}");
			}
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Kern_ansi_compiles_and_runs_in_a_project()
	{
		string directory = Path.Combine(Path.GetTempPath(), "kern-std-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(directory, "src"));
		File.WriteAllText(Path.Combine(directory, "src", "app.kern"), """
			use kern.ansi[red, strip]
			fun main {
				println(strip(red("x")))
			}
			""");
		try
		{
			var output = new StringWriter();
			var error = new StringWriter();
			TextWriter previous = Console.Out;
			Console.SetOut(output);
			int code;
			try
			{
				code = KernCli.Run(["run", directory], output, error);
			}
			finally
			{
				Console.SetOut(previous);
			}

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error.ToString());
			Assert.Equal("x\n", output.ToString().Replace("\r\n", "\n"));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}
}
