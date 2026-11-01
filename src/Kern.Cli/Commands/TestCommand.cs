using Kern.Cli.Build;
using Kern.Cli.Infrastructure;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc test &lt;файл.kern | папка&gt;</c>: компилирует и запускает функции с
/// <c>#[test]</c> без аргументов. Для проекта с <c>build.kern</c> тесты всех целей;
/// для папки по соглашению — единственной цели. Код возврата 0 — все тесты прошли,
/// 1 — ошибка компиляции или теста, 2 — неверные аргументы.
/// </summary>
internal static class TestCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		if (!SourceArguments.TryParse(
			args, "test", allowOutput: false, allowProgramArguments: false, stderr,
			out string path, out SourceKind kind, out _, out _, out CliOptions options))
			return 2;

		if (kind == SourceKind.Project)
			return TestProject(path, options, stdout, stderr);

		CompileResult result = SourceCompiler.Compile(path, kind, options.Apply(CompileOptions.Default));
		SourceCompiler.Report(result, stderr);
		return result.Success ? TestRunner.Run(result.Program!, stdout) : 1;
	}

	private static int TestProject(string path, CliOptions options, TextWriter stdout, TextWriter stderr)
	{
		try
		{
			BuildSession session = BuildSession.Open(path);
			session.OptimizeOverride = options.Optimize;
			session.ProfileName = options.Profile;
			return TestRunner.Run(session, target: null, stdout, stderr);
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}
	}
}
