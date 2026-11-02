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
			Assert.Equal(Canonical(directory), stdout.ToString().Trim());
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	/// <summary>
	/// Разрешает символические ссылки в компонентах пути: на macOS временный каталог
	/// возвращается как <c>/var/…</c>, а <c>getcwd()</c> в программе — уже как
	/// <c>/private/var/…</c>. Без этого сравнение падало только в CI macOS.
	/// </summary>
	private static string Canonical(string path)
	{
		string full = Path.GetFullPath(path);
		if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
			return full;
		string root = Path.GetPathRoot(full)!;
		string current = root;
		foreach (string part in full[root.Length..].Split(
			[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
			StringSplitOptions.RemoveEmptyEntries))
		{
			current = Path.Combine(current, part);
			FileSystemInfo info = Directory.Exists(current)
				? new DirectoryInfo(current)
				: new FileInfo(current);
			if (info.LinkTarget is { } target)
				current = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, target));
		}
		return current;
	}
}
