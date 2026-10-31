using Kern.Bytecode.Serialization;
using Kern.Cli.Build;
using Kern.Cli.Commands;
using Kern.Cli.Infrastructure;

namespace Kern.Cli;

internal static class Program
{
	/// <summary>Имя файла байткода рядом с хостом (режим автономного exe).</summary>
	public const string EmbeddedProgram = "program.kbc";

	public const string Version = "0.2.0";

	private static readonly HashSet<string> WorkspaceCommands = ["build", "run", "test", "exe", "clean", "tasks", "do"];

	public static int Main(string[] args)
	{
		ConsoleSetup.UseUtf8();

		// автономный режим: рядом лежит program.kbc
		string embedded = Path.Combine(AppContext.BaseDirectory, EmbeddedProgram);
		if (File.Exists(embedded) && (args.Length == 0 || args[0] != "--kernc"))
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			int code = Pipeline.CreateVm(KbcReader.Read(File.ReadAllBytes(embedded))).RunMain(args);
			if (Environment.GetEnvironmentVariable("KERN_TIME") is { Length: > 0 }) Console.Error.WriteLine($"[время] выполнение: {CliCommands.FormatElapsed(sw.Elapsed)}");
			return code;
		}

		if (args.Length == 0)
		{
			Console.Out.WriteLine(HelpText.General);
			return 1;
		}

		string command = args[0];
		if (command is "-h" or "--help") command = "help";
		if (command is "-v" or "--version") command = "version";

		var rest = args.Skip(1).ToList();
		int dash = rest.IndexOf("--");
		string[] programArgs = dash >= 0 ? [.. rest.Skip(dash + 1)] : [];
		if (dash >= 0) rest = rest.Take(dash).ToList();

		if (command == "version")
		{
			Console.Out.WriteLine($"kernc {Version}");
			return 0;
		}
		if (command == "help")
		{
			string? topic = rest.FirstOrDefault(a => !a.StartsWith('-'));
			if (topic is null) Console.Out.WriteLine(HelpText.General);
			else if (HelpText.TryGet(topic, out var text)) Console.Out.WriteLine(text);
			else
			{
				Console.Error.WriteLine($"error: нет справки по '{topic}'\n\n{HelpText.General}");
				return 2;
			}
			return 0;
		}
		if (rest.Any(a => a is "-h" or "--help"))
		{
			Console.Out.WriteLine(HelpText.TryGet(command, out var text) ? text : HelpText.General);
			return 0;
		}

		int opt = 1;
		bool showTime = false;
		string? output = null;
		var positional = new List<string>();
		for (int i = 0; i < rest.Count; i++)
		{
			if (rest[i] is "-O0" or "-O1" or "-O2") opt = rest[i][2] - '0';
			else if (rest[i] is "--time") showTime = true;
			else if (rest[i] is "-o" && i + 1 < rest.Count) output = rest[++i];
			else positional.Add(rest[i]);
		}

		var total = System.Diagnostics.Stopwatch.StartNew();
		try
		{
			if (!HelpText.TryGet(command, out _)) return Unknown(command);

			// `do` принимает задачи, остальные команды — путь первым аргументом
			string path = command == "do"
				? positional.FirstOrDefault(Directory.Exists) ?? "."
				: positional.Count > 0 ? positional[0] : ".";

			if (WorkspaceCommands.Contains(command) && Directory.Exists(path) && BuildScript.Exists(path))
			{
				var extra = command == "do" ? positional.Where(p => p != path).ToList() : positional.Skip(1).ToList();
				return Timed(showTime, total, WorkspaceCommand.Run(command, path, extra, programArgs, opt));
			}

			if (command == "do") return Fail("в этой папке нет build.kern");
			if (command == "tasks") return Fail("в этой папке нет build.kern");
			if (command == "clean") return Fail("в этой папке нет build.kern (нечего очищать)");
			if (positional.Count == 0 && !Directory.Exists(".")) return Fail($"команде '{command}' нужен путь");

			return Timed(showTime, total, command switch
			{
				"run" => CliCommands.Run(path, programArgs, opt, showTime),
				"check" => CliCommands.Check(path),
				"build" => CliCommands.Build(path, output, opt),
				"exe" => CliCommands.Exe(path, output, opt),
				"dis" => CliCommands.Disassemble(path, opt),
				"dll" => CliCommands.Dll(path, output, opt),
				"fmt" => FmtCommand.Run(positional),
				"repl" => ReplCommand.Run(opt),
				"test" => CliCommands.Test(path, opt),
				_ => Unknown(command),
			});
		}
		catch (Kern.Core.KernCompileException e)
		{
			Console.Error.WriteLine(e.Message);
			return 1;
		}
		catch (InvalidOperationException e)
		{
			Console.Error.WriteLine("error: " + e.Message);
			return 1;
		}
	}

	/// <summary>`--time`: общее время команды в stderr (stdout программы остаётся чистым).</summary>
	private static int Timed(bool show, System.Diagnostics.Stopwatch total, int code)
	{
		if (show) Console.Error.WriteLine($"[время] всего: {CliCommands.FormatElapsed(total.Elapsed)}");
		return code;
	}

	private static int Fail(string message)
	{
		Console.Error.WriteLine("error: " + message);
		return 2;
	}

	private static int Unknown(string command)
	{
		Console.Error.WriteLine($"error: неизвестная команда '{command}'\n\n{HelpText.General}");
		return 2;
	}
}
