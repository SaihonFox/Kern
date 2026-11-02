using Kern.Cli.Build;
using Kern.Cli.Infrastructure;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc check &lt;файл.kern | папка&gt;</c>: только компилирует и печатает все диагностики,
/// ничего не записывает. Код возврата 0 — ошибок нет, 1 — ошибка компиляции или чтения,
/// 2 — неверные аргументы. Для проекта с <c>build.kern</c> проверяется app-цель (без записи).
/// </summary>
internal static class CheckCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		_ = stdout;

		if (!SourceArguments.TryParse(
			args, "check", allowOutput: false, allowProgramArguments: false, stderr,
			out string path, out SourceKind kind, out _, out _, out CliOptions options))
			return 2;

		if (kind == SourceKind.Project && File.Exists(Path.Combine(path, BuildSession.ScriptName)))
			return CheckDirectory(path, options, stderr);
		if (kind == SourceKind.Project && IsContainer(path))
			return CheckContainer(path, options, stderr);

		CompileResult result = SourceCompiler.Compile(path, kind, options.Apply(CompileOptions.Default));
		SourceCompiler.Report(result, stderr);
		return result.Success ? 0 : 1;
	}

	private static bool IsContainer(string path)
	{
		if (File.Exists(Path.Combine(path, BuildSession.ScriptName)) ||
			Directory.Exists(Path.Combine(path, "src")) ||
			Directory.EnumerateFiles(path, "*.kern").Any())
			return false;
		return Directory.EnumerateDirectories(path).Any(HasSources);
	}

	private static bool HasSources(string directory) =>
		File.Exists(Path.Combine(directory, BuildSession.ScriptName)) ||
		Directory.Exists(Path.Combine(directory, "src")) ||
		Directory.EnumerateFiles(directory, "*.kern").Any();

	private static int CheckContainer(string path, CliOptions options, TextWriter stderr)
	{
		foreach (string directory in Directory.EnumerateDirectories(path).OrderBy(item => item, StringComparer.Ordinal))
		{
			if (!HasSources(directory))
				continue;
			int code = CheckDirectory(directory, options, stderr);
			if (code != 0)
				return code;
		}

		return 0;
	}

	private static int CheckDirectory(string directory, CliOptions options, TextWriter stderr)
	{
		directory = Path.GetFullPath(directory);
		int prebuild = PrebuildRunner.Run(directory, stderr);
		if (prebuild != 0)
			return prebuild;

		string? previous = null;
		try
		{
			previous = Environment.CurrentDirectory;
			Directory.SetCurrentDirectory(directory);
			if (File.Exists(Path.Combine(directory, BuildSession.ScriptName)))
				return CheckProject(directory, options, stderr);

			CompileResult result = SourceCompiler.Compile(directory, SourceKind.Project, options.Apply(CompileOptions.Default));
			SourceCompiler.Report(result, stderr);
			return result.Success ? 0 : 1;
		}
		finally
		{
			if (previous is not null)
				Directory.SetCurrentDirectory(previous);
		}
	}

	private static int CheckProject(string path, CliOptions options, TextWriter stderr)
	{
		IReadOnlyList<BuildTargetResult> results;
		try
		{
			BuildSession session = BuildSession.Open(path);
			session.OptimizeOverride = options.Optimize;
			session.ProfileName = options.Profile;
			// Проверяем все цели, включая библиотечные: `kernc check` не должен требовать
			// app-цель у проекта-библиотеки (`kernc new lib`).
			results = BuildTargetCompiler.BuildAllCheck(session);
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}

		var success = true;
		foreach (BuildTargetResult result in results)
		{
			SourceCompiler.Report(result.Compile, stderr);
			success &= result.Compile.Success;
		}

		return success ? 0 : 1;
	}
}
