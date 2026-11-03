using Kern.Cli.Debug;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc debug &lt;файл.kern | папка&gt; [аргумент ...]</c>: запускает программу под
/// интерактивным отладчиком (docs/DEBUGGER.md). Аргументы после исходника — как у <c>run</c>.
/// </summary>
internal static class DebugCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		stdout.WriteLine("отладка: help — команды; break <строка|файл:строка|функция>, step, next, continue, locals, stack, quit");
		var debugger = new ConsoleDebugger(Console.In, stdout);
		return RunCommand.Run(args, stdout, stderr, debugger, "debug");
	}
}
