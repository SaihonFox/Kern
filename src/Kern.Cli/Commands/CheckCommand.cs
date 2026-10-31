using Kern.Cli.Infrastructure;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc check &lt;файл.kern | папка&gt;</c>: только компилирует и печатает все диагностики,
/// ничего не записывает. Код возврата 0 — ошибок нет, 1 — ошибка компиляции или чтения,
/// 2 — неверные аргументы.
/// </summary>
internal static class CheckCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		_ = stdout;

		if (!SourceArguments.TryParse(
			args, "check", allowOutput: false, allowProgramArguments: false, stderr,
			out string path, out SourceKind kind, out _, out _))
			return 2;

		CompileResult result = SourceCompiler.Compile(path, kind);
		SourceCompiler.Report(result, stderr);
		return result.Success ? 0 : 1;
	}
}
