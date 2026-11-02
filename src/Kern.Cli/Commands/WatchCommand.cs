using System.Diagnostics;
using System.Reflection;
using Kern.Cli.Build;
using Kern.Cli.Infrastructure;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc run --watch &lt;файл|папка&gt;</c>: запускает программу в дочернем процессе
/// и перезапускает её при изменении <c>.kern</c>-исходников (не hot reload).
/// </summary>
internal static class WatchCommand
{
	private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
	private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		string[] runArgs = [.. args.Where(argument => argument != "--watch")];
		if (runArgs.Length == 0)
		{
			stderr.WriteLine("ошибка: использование: kernc run --watch <файл.kern|папка> [аргументы]");
			return 2;
		}

		// Проверяем аргументы тем же разбором, что и `run`: ошибки видны сразу.
		if (!SourceArguments.TryParse(
			runArgs, "run", allowOutput: false, allowProgramArguments: true, stderr,
			out string path, out SourceKind kind, out _, out IReadOnlyList<string> programArguments,
			out _))
			return 2;

		WatchSet watched = WatchSet.Create(path, kind);
		if (watched.Count == 0)
		{
			stderr.WriteLine($"ошибка: не найдены .kern-файлы для наблюдения: {path}");
			return 1;
		}

		bool stop = false;
		ConsoleCancelEventHandler cancel = (_, eventArgs) =>
		{
			stop = true;
			eventArgs.Cancel = true;
		};
		Console.CancelKeyPress += cancel;
		Process? child = null;
		try
		{
			child = Start(runArgs);
			stderr.WriteLine($"наблюдаю: {string.Join(", ", watched.Paths)}");
			while (!stop)
			{
				Thread.Sleep(Poll);
				WatchSet next = WatchSet.Create(path, kind);
				if (!next.Differs(watched))
					continue;

				Thread.Sleep(Debounce);
				watched = WatchSet.Create(path, kind);
				stderr.WriteLine("изменение: перезапуск");
				Stop(child);
				child = Start(runArgs);
			}
		}
		finally
		{
			Console.CancelKeyPress -= cancel;
			Stop(child);
		}

		return 0;
	}

	/// <summary>Снимок исходников для тестов и цикла наблюдения.</summary>
	internal static WatchSet Snapshot(string path, SourceKind kind) => WatchSet.Create(path, kind);

	private static Process Start(string[] runArgs)
	{
		var start = new ProcessStartInfo
		{
			UseShellExecute = false,
			WorkingDirectory = Environment.CurrentDirectory,
		};
		string process = Environment.ProcessPath ?? "kernc";
		string? entry = Assembly.GetEntryAssembly()?.Location;
		if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
			!string.IsNullOrEmpty(entry))
		{
			start.FileName = process;
			start.ArgumentList.Add(entry);
		}
		else
		{
			start.FileName = process;
		}

		start.ArgumentList.Add("run");
		foreach (string argument in runArgs)
			start.ArgumentList.Add(argument);
		return Process.Start(start) ?? throw new InvalidOperationException("не удалось запустить kernc");
	}

	private static void Stop(Process? process)
	{
		if (process is null || process.HasExited)
			return;
		try
		{
			process.Kill(entireProcessTree: true);
			process.WaitForExit(5000);
		}
		catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			// Процесс уже завершился — это нормально.
		}
	}
}

/// <summary>Набор наблюдаемых исходников и их отметки времени/размера.</summary>
internal sealed class WatchSet
{
	private readonly Dictionary<string, (long Ticks, long Length)> _files;

	private WatchSet(Dictionary<string, (long Ticks, long Length)> files) => _files = files;

	public int Count => _files.Count;

	public IEnumerable<string> Paths => _files.Keys;

	public static WatchSet Create(string path, SourceKind kind)
	{
		var files = new Dictionary<string, (long Tick, long Length)>(StringComparer.OrdinalIgnoreCase);
		if (kind == SourceKind.File)
		{
			Add(files, path);
			return new WatchSet(files);
		}

		string root = Path.GetFullPath(path);
		string script = Path.Combine(root, BuildSession.ScriptName);
		Add(files, script);
		string source = Path.Combine(root, "src");
		string directory = Directory.Exists(source) ? source : root;
		if (Directory.Exists(directory))
		{
			foreach (string file in Directory.EnumerateFiles(directory, "*.kern", SearchOption.AllDirectories))
				Add(files, file);
		}

		return new WatchSet(files);
	}

	public bool Differs(WatchSet other)
	{
		if (_files.Count != other._files.Count)
			return true;
		foreach (var (path, stamp) in _files)
		{
			if (!other._files.TryGetValue(path, out var current) || current != stamp)
				return true;
		}

		return false;
	}

	private static void Add(Dictionary<string, (long Tick, long Length)> files, string path)
	{
		if (!File.Exists(path))
			return;
		var info = new FileInfo(path);
		files[Path.GetFullPath(path)] = (info.LastWriteTimeUtc.Ticks, info.Length);
	}
}
