using Kern.Cli.Debug;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc debug &lt;файл.kern | папка&gt; [аргумент ...]</c>: запускает программу под
/// интерактивным отладчиком (docs/DEBUGGER.md). С <c>--dap &lt;порт&gt;</c> вместо консоли
/// поднимается DAP-сервер для IDE/удалённой отладки. Аргументы после исходника — как у
/// <c>run</c>.
/// </summary>
internal static class DebugCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		int? port = DapPort(args);
		if (port is { } dapPort)
		{
			var debugger = new DapDebugger(dapPort);
			try
			{
				return RunCommand.Run(args, stdout, stderr, debugger, "debug");
			}
			finally
			{
				debugger.Dispose();
			}
		}

		stdout.WriteLine("отладка: help — команды; break <строка|файл:строка|функция>, step, next, continue, locals, stack, quit");
		var console = new ConsoleDebugger(Console.In, stdout);
		return RunCommand.Run(args, stdout, stderr, console, "debug");
	}

	private static int? DapPort(string[] args)
	{
		for (var index = 0; index < args.Length - 1; index++)
			if (args[index] == "--dap" && int.TryParse(args[index + 1], out int port))
				return port;
		return null;
	}
}
