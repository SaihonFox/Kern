using System.Diagnostics;
using Kern.Bytecode;
using Kern.Cli.Infrastructure;

namespace Kern.Cli.Build;

/// <summary>
/// <c>kern build --native [--target &lt;rid&gt;]</c>: собирает программу в один исполняемый
/// файл, которому не нужен установленный .NET. Байткод встраивается ресурсом
/// <c>program.kbc</c> в самодостаточный однофайловый хост (VM + std), поэтому программа
/// запускается на Windows/Linux/macOS без SDK и без соседних файлов.
/// </summary>
internal static class NativeCompiler
{
	public static string BuildFile(string source, string? output, CliOptions options, TextWriter stderr)
	{
		CompileResult compiled = SourceCompiler.Compile(source, options.Apply(CompileOptions.Default));
		SourceCompiler.Report(compiled, stderr);
		if (!compiled.Success)
			throw new BuildException("программа не скомпилировалась");

		string name = Path.GetFileNameWithoutExtension(source);
		string temporary = Path.Combine(Path.GetTempPath(), $"kern-native-{Guid.NewGuid():N}", name + ".kbc");
		Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
		File.WriteAllBytes(temporary, KbcWriter.Write(compiled.Program!.Module));
		try
		{
			string location = output ??
				Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, "dist", "native");
			return Publish(temporary, name, location, options, stderr);
		}
		finally
		{
			try
			{
				Directory.Delete(Path.GetDirectoryName(temporary)!, recursive: true);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	public static string BuildProject(BuildSession session, string? output, CliOptions options, TextWriter stderr)
	{
		ArgumentNullException.ThrowIfNull(session);
		TargetModel target = session.Workspace.DefaultAppTarget()
			?? throw new BuildException("в build.kern нет app-цели для --native");
		string directory = output is null
			? Path.Combine(session.Root, "dist", "native")
			: Path.GetFullPath(output);
		return BuildProjectInto(session, target, directory, options, stderr);
	}

	/// <summary>Собрать выбранную app-цель в указанный каталог одним исполняемым файлом.</summary>
	public static string BuildProjectInto(
		BuildSession session,
		TargetModel target,
		string directory,
		CliOptions options,
		TextWriter stderr)
	{
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(target);
		if (target.IsLibrary)
			throw new BuildException($"цель '{target.Name}' — библиотека; --native требует app-цель");

		string temporary = Path.Combine(Path.GetTempPath(), $"kern-native-{Guid.NewGuid():N}", target.Name + ".kbc");
		Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
		BuildTargetResult result = BuildTargetCompiler.Compile(session, target, temporary, write: true);
		SourceCompiler.Report(result.Compile, stderr);
		if (!result.Compile.Success)
			throw new BuildException($"цель '{target.Name}' не скомпилировалась");
		try
		{
			return Publish(temporary, target.Name, Path.GetFullPath(directory), options, stderr);
		}
		finally
		{
			try
			{
				Directory.Delete(Path.GetDirectoryName(temporary)!, recursive: true);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	private static string Publish(
		string bytecode,
		string programName,
		string? output,
		CliOptions options,
		TextWriter stderr)
	{
		string project = FindProject()
			?? throw new BuildException(
				"--native требует исходники kernc (src/Kern.Cli/Kern.Cli.csproj): из установленного архива используйте kernc exe");
		string rid = options.TargetRid ?? CurrentRid();
		string directory = output ??
			Path.Combine(Path.GetDirectoryName(bytecode)!, "native");
		Directory.CreateDirectory(directory);

		var start = new ProcessStartInfo("dotnet")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		start.ArgumentList.Add("publish");
		start.ArgumentList.Add(project);
		start.ArgumentList.Add("-c");
		start.ArgumentList.Add("Release");
		start.ArgumentList.Add("-r");
		start.ArgumentList.Add(rid);
		start.ArgumentList.Add("--self-contained");
		start.ArgumentList.Add("true");
		start.ArgumentList.Add("-p:PublishSingleFile=true");
		start.ArgumentList.Add("-p:PublishTrimmed=true");
		start.ArgumentList.Add("-p:IncludeNativeLibrariesForSelfExtract=true");
		start.ArgumentList.Add($"-p:KernEmbedProgram={bytecode}");
		start.ArgumentList.Add("-o");
		start.ArgumentList.Add(directory);

		using var process = Process.Start(start)
			?? throw new BuildException("не удалось запустить dotnet publish");
		string stdout = process.StandardOutput.ReadToEnd();
		string errorText = process.StandardError.ReadToEnd();
		process.WaitForExit();
		if (process.ExitCode != 0)
		{
			stderr.WriteLine(stdout);
			stderr.WriteLine(errorText);
			throw new BuildException($"dotnet publish завершился с кодом {process.ExitCode}");
		}

		string published = Path.Combine(directory, OperatingSystem.IsWindows() ? "kernc.exe" : "kernc");
		if (!File.Exists(published))
			throw new BuildException($"после публикации не найден хост: {published}");
		string host = Path.Combine(directory, OperatingSystem.IsWindows() ? programName + ".exe" : programName);
		if (!string.Equals(published, host, StringComparison.Ordinal))
		{
			File.Move(published, host, overwrite: true);
			foreach (string sidecar in Directory.EnumerateFiles(directory, "*.pdb"))
				File.Delete(sidecar);
		}
		return host;
	}

	/// <summary>RID текущей платформы (без вызова `dotnet --info`).</summary>
	private static string CurrentRid()
	{
		string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
		string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
		{
			System.Runtime.InteropServices.Architecture.X64 => "x64",
			System.Runtime.InteropServices.Architecture.X86 => "x86",
			System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
			System.Runtime.InteropServices.Architecture.Arm => "arm",
			_ => "x64",
		};
		return $"{os}-{architecture}";
	}

	/// <summary>Репозиторий разработки: ищем Kern.Cli.csproj от текущего каталога вверх.</summary>
	private static string? FindProject()
	{
		string? directory = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(directory))
		{
			string candidate = Path.Combine(directory, "src", "Kern.Cli", "Kern.Cli.csproj");
			if (File.Exists(candidate))
				return candidate;
			directory = Path.GetDirectoryName(directory);
		}

		directory = Environment.CurrentDirectory;
		while (!string.IsNullOrEmpty(directory))
		{
			string candidate = Path.Combine(directory, "src", "Kern.Cli", "Kern.Cli.csproj");
			if (File.Exists(candidate))
				return candidate;
			directory = Path.GetDirectoryName(directory);
		}

		return null;
	}
}
