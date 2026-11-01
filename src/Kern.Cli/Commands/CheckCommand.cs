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
			return CheckProject(path, options, stderr);

		CompileResult result = SourceCompiler.Compile(path, kind, options.Apply(CompileOptions.Default));
		SourceCompiler.Report(result, stderr);
		return result.Success ? 0 : 1;
	}

	private static int CheckProject(string path, CliOptions options, TextWriter stderr)
	{
		BuildTargetResult built;
		try
		{
			BuildSession session = BuildSession.Open(path);
			session.OptimizeOverride = options.Optimize;
			built = BuildTargetCompiler.CompileApp(session);
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}

		SourceCompiler.Report(built.Compile, stderr);
		return built.Compile.Success ? 0 : 1;
	}
}
