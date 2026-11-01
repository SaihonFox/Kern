using Kern.Cli.Infrastructure;

namespace Kern.Cli.Build;

/// <summary>
/// <c>kernc exe</c> и встроенная задача <c>&lt;цель&gt;:exe</c> (docs/BUILDER.md §2):
/// <c>dist/&lt;имя&gt;[.exe]</c> = хост kernc + соседний <c>program.kbc</c>. Хост при запуске
/// без команды подхватывает <c>program.kbc</c> рядом с собой и выполняет его.
/// </summary>
internal static class ExeCompiler
{
	public static string Build(BuildSession session, TargetModel? target)
	{
		ArgumentNullException.ThrowIfNull(session);
		TargetModel selected = target ?? session.Workspace.DefaultAppTarget()
			?? throw new BuildException("в build.kern нет app-цели для exe");
		if (selected.IsLibrary)
			throw new BuildException($"цель '{selected.Name}' — библиотека; exe требует app-цель");

		string dist = Path.Combine(session.Root, "dist");
		try
		{
			Directory.CreateDirectory(dist);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new BuildException($"не удалось создать {dist}: {exception.Message}");
		}

		string program = Path.Combine(dist, "program.kbc");
		BuildTargetResult compiled = BuildTargetCompiler.Compile(session, selected, program, write: true);
		if (!compiled.Compile.Success)
			throw new BuildException(Describe(compiled.Compile, selected));
		CopyHost(dist, HostName(selected.Name));
		return Path.Combine(dist, HostName(selected.Name));
	}

	private static string HostName(string project) =>
		OperatingSystem.IsWindows() ? project + ".exe" : project;

	private static void CopyHost(string dist, string hostName)
	{
		string source = AppContext.BaseDirectory;
		string[] files = Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly);
		foreach (string file in files)
		{
			string name = Path.GetFileName(file);
			if (!name.StartsWith("kernc.", StringComparison.OrdinalIgnoreCase) &&
				!name.StartsWith("Kern.", StringComparison.OrdinalIgnoreCase) &&
				!string.Equals(name, "kernc.exe", StringComparison.OrdinalIgnoreCase))
				continue;
			File.Copy(file, Path.Combine(dist, name), overwrite: true);
		}

		string host = Path.Combine(source, OperatingSystem.IsWindows() ? "kernc.exe" : "kernc");
		if (!File.Exists(host))
			throw new BuildException($"не найден хост kernc рядом с CLI: {host}");
		File.Copy(host, Path.Combine(dist, hostName), overwrite: true);
	}

	private static string Describe(CompileResult result, TargetModel target)
	{
		var lines = new List<string>();
		if (result.FailureMessage is { } failure)
			lines.Add(failure);
		lines.AddRange(result.ProjectMessages);
		lines.AddRange(result.NativeProblems);
		return lines.Count > 0
			? string.Join(Environment.NewLine, lines)
			: $"цель '{target.Name}' не скомпилировалась";
	}
}
