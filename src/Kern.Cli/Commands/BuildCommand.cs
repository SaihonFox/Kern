using Kern.Bytecode;
using Kern.Cli.Build;
using Kern.Cli.Infrastructure;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc build &lt;файл.kern | папка&gt; [-o &lt;файл.kbc&gt;]</c>: компилирует один файл,
/// проект по соглашению или проект с <c>build.kern</c> и пишет байткод. Папка с
/// <c>build.kern</c> собирается по целям этого сценария (docs/BUILDER.md §3): app-цель
/// пишется в <c>&lt;папка&gt;/out/&lt;цель&gt;.kbc</c>, lib-цель — туда же без точки входа
/// и с интерфейсными заглушками (готовый файл для <c>t.kbc</c>).
/// Без <c>build.kern</c> действует прежнее соглашение: <c>&lt;папка&gt;/out/&lt;имя&gt;.kbc</c>.
/// </summary>
internal static class BuildCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		if (!SourceArguments.TryParse(
			args, "build", allowOutput: true, allowProgramArguments: false, stderr,
			out string path, out SourceKind kind, out string? output, out _, out CliOptions options))
			return 2;

		if (options.Native)
			return BuildNative(path, kind, output, options, stdout, stderr);

		if (kind == SourceKind.Project)
			return BuildProject(path, output, options, stdout, stderr);

		CompileResult result = SourceCompiler.Compile(path, options.Apply(CompileOptions.Default));		SourceCompiler.Report(result, stderr);
		if (!result.Success)
			return 1;

		string target = output ?? Path.ChangeExtension(path, ".kbc");
		return Write(result, target, stdout, stderr);
	}

	private static int BuildProject(string directory, string? output, CliOptions options, TextWriter stdout, TextWriter stderr)
	{
		BuildSession session;
		try
		{
			session = BuildSession.Open(directory);
			session.OptimizeOverride = options.Optimize;
			session.ProfileName = options.Profile;
			session.WarningsAsErrorsOverride = options.DenyWarnings ? true : null;
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}

		IReadOnlyList<BuildTargetResult> results;
		try
		{
			results = BuildTargetCompiler.BuildAll(session, output);
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}

		foreach (BuildTargetResult result in results)
			SourceCompiler.Report(result.Compile, stderr);
		if (results.Any(result => !result.Compile.Success && !result.UpToDate))
			return 1;

		foreach (BuildTargetResult result in results)
			if (result.OutputPath is { } written)
				stdout.WriteLine(written);
		return 0;
	}

	private static int BuildNative(
		string path,
		SourceKind kind,
		string? output,
		CliOptions options,
		TextWriter stdout,
		TextWriter stderr)
	{
		try
		{
			string host = kind == SourceKind.Project
				? NativeCompiler.BuildProject(OpenSession(path, options), output, options, stderr)
				: NativeCompiler.BuildFile(path, output, options, stderr);
			stdout.WriteLine(host);
			return 0;
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}
	}

	private static BuildSession OpenSession(string path, CliOptions options)
	{
		BuildSession session = BuildSession.Open(path);
		session.OptimizeOverride = options.Optimize;
		session.ProfileName = options.Profile;
		session.WarningsAsErrorsOverride = options.DenyWarnings ? true : null;
		return session;
	}

	private static int Write(CompileResult result, string target, TextWriter stdout, TextWriter stderr)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
			File.WriteAllBytes(target, KbcWriter.Write(result.Program!.Module));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			stderr.WriteLine($"ошибка: не удалось записать {target}: {ex.Message}");
			return 1;
		}

		stdout.WriteLine(target);
		return 0;
	}
}
