using System.Runtime.InteropServices;
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
	/// Канонический путь: на macOS <c>/var</c> — символическая ссылка на
	/// <c>/private/var</c>, а <c>getcwd()</c> в программе возвращает уже реальный путь.
	/// <c>realpath(3)</c> разрешает все компоненты; на Windows достаточно GetFullPath.
	/// </summary>
	private static string Canonical(string path)
	{
		if (OperatingSystem.IsWindows())
			return Path.GetFullPath(path);

		IntPtr resolved = realpath(path, IntPtr.Zero);
		if (resolved == IntPtr.Zero)
			return Path.GetFullPath(path);
		try
		{
			return Marshal.PtrToStringUTF8(resolved) ?? Path.GetFullPath(path);
		}
		finally
		{
			free(resolved);
		}
	}

	[DllImport("libc", SetLastError = true, EntryPoint = "realpath")]
	private static extern IntPtr realpath(string path, IntPtr resolved);

	[DllImport("libc", EntryPoint = "free")]
	private static extern void free(IntPtr pointer);
}
