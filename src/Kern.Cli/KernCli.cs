using Kern.Cli.Commands;
using Kern.Cli.Infrastructure;

namespace Kern.Cli;

/// <summary>
/// Разбор аргументов <c>kernc</c>. Не работает с <see cref="Console"/> напрямую, поэтому
/// команды можно проверять из тестов без запуска процесса.
/// Коды возврата: 0 — успех, 1 — ошибка в файле, 2 — неверные аргументы.
/// </summary>
public static class KernCli
{
	public const string Version = "0.1.0";

	public static int Run(string[] args) => Run(args, Console.Out, Console.Error);

	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(stdout);
		ArgumentNullException.ThrowIfNull(stderr);

		// `kernc exe` кладёт рядом с хостом program.kbc: без команды запускаем его.
		if (EmbeddedProgram.Exists && (args.Length == 0 || !KnownCommand(args[0])))
			return EmbeddedProgram.Run(args, stderr);

		if (args.Length == 0)
		{
			stdout.WriteLine(HelpText.General);
			return 1;
		}

		string command = args[0];
		if (command is "-h" or "--help") command = "help";
		else if (command is "-v" or "--version") command = "version";

		// `--time` печатает общее время команды в stderr (spec §16, раунд 12).
		var watch = args.Contains("--time") ? System.Diagnostics.Stopwatch.StartNew() : null;
		int code = command switch
		{
			"help" => Help(args[1..], stdout, stderr),
			"version" => ShowVersion(stdout),
			"run" => RunCommand.Run(args[1..], stdout, stderr),
			"check" => CheckCommand.Run(args[1..], stdout, stderr),
			"test" => TestCommand.Run(args[1..], stdout, stderr),
			"build" => BuildCommand.Run(args[1..], stdout, stderr),
			"dll" => DllCommand.Run(args[1..], stdout, stderr),
			"exe" => ExeCommand.Run(args[1..], stdout, stderr),
			"tasks" => TasksCommand.Run(args[1..], stdout, stderr),
			"do" => DoCommand.Run(args[1..], stdout, stderr),
			"clean" => CleanCommand.Run(args[1..], stdout, stderr),
			"fmt" => FmtCommand.Run(args[1..], stdout, stderr),
			"repl" => ReplCommand.Run(args[1..], stdout, stderr),
			"dis" => DisCommand.Run(args[1..], stdout, stderr),
			_ => Unknown(command, stderr),
		};

		if (watch is not null)
			stderr.WriteLine($"время: {watch.ElapsedMilliseconds} мс");
		return code;
	}

	private static int Help(string[] args, TextWriter stdout, TextWriter stderr)
	{
		if (args.Length == 0)
		{
			stdout.WriteLine(HelpText.General);
			return 0;
		}

		if (HelpText.TryGet(args[0], out string? text))
		{
			stdout.WriteLine(text);
			return 0;
		}

		stderr.WriteLine($"ошибка: нет справки по '{args[0]}'");
		stderr.WriteLine("Использование: kernc help [команда]");
		return 2;
	}

	private static int ShowVersion(TextWriter stdout)
	{
		stdout.WriteLine($"kernc {Version}");
		return 0;
	}

	private static int Unknown(string command, TextWriter stderr)
	{
		stderr.WriteLine($"ошибка: неизвестная команда '{command}'");
		stderr.WriteLine("Использование: kernc <команда> [аргументы]; список команд: kernc help");
		return 2;
	}

	private static bool KnownCommand(string command) => command is
		"help" or "version" or "run" or "check" or "test" or "build" or "dll" or "exe" or
		"tasks" or "do" or "clean" or "fmt" or "repl" or "dis" or
		"-h" or "--help" or "-v" or "--version";
}
