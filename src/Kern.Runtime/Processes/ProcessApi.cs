using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Processes;

/// <summary>
/// <c>process_output</c> (spec §16, раунд 27): код возврата и захваченные потоки.
/// </summary>
[KernType("process_output")]
public sealed record KernProcessOutput(int Code, string Stdout, string Stderr);

/// <summary>Члены <c>process_output</c>; первый параметр — <c>self</c>.</summary>
[KernType("process_output")]
public static class ProcessOutputMembers
{
	/// <summary><c>o.code</c>: код возврата; ненулевой код — не ошибка.</summary>
	[KernProperty]
	public static int Code(KNativeBox self) => Require(self).Code;

	/// <summary><c>o.stdout</c>: весь стандартный вывод.</summary>
	[KernProperty]
	public static string Stdout(KNativeBox self) => Require(self).Stdout;

	/// <summary><c>o.stderr</c>: весь поток ошибок.</summary>
	[KernProperty]
	public static string Stderr(KNativeBox self) => Require(self).Stderr;

	internal static KernProcessOutput Require(KNativeBox self) => self.Value switch
	{
		KernProcessOutput output => output,
		_ => throw new KernPanic($"process_output: ожидался результат процесса, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>
/// <c>process_handle</c> (spec §16): запущенный процесс с перенаправленными потоками.
/// Строки читаются из очередей, которые наполняют асинхронные обработчики, поэтому
/// <c>wait()</c> не блокируется на переполнении буферов.
/// </summary>
[KernType("process_handle")]
public sealed class KernProcessHandle
{
	private readonly Process _process;
	private readonly BlockingCollection<string> _stdout = [];
	private readonly BlockingCollection<string> _stderr = [];

	internal KernProcessHandle(Process process)
	{
		_process = process;
		_process.OutputDataReceived += (_, args) => Enqueue(_stdout, args.Data);
		_process.ErrorDataReceived += (_, args) => Enqueue(_stderr, args.Data);
	}

	internal int Pid => _process.Id;

	internal void Start()
	{
		_process.Start();
		_process.BeginOutputReadLine();
		_process.BeginErrorReadLine();
	}

	internal int Wait()
	{
		_process.WaitForExit();
		return _process.ExitCode;
	}

	internal void Kill()
	{
		try
		{
			_process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// Процесс уже завершился — kill() не ошибка.
		}
		catch (NotSupportedException)
		{
			_process.Kill();
		}
	}

	internal int? TryWait()
	{
		try
		{
			return _process.HasExited ? _process.ExitCode : null;
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	internal string? ReadStdoutLine() => Take(_stdout);

	internal string? ReadStderrLine() => Take(_stderr);

	internal void WriteStdin(string text)
	{
		_process.StandardInput.Write(text);
		_process.StandardInput.Flush();
	}

	internal void CloseStdin() => _process.StandardInput.Close();

	private static void Enqueue(BlockingCollection<string> queue, string? line)
	{
		if (line is null)
		{
			if (!queue.IsAddingCompleted)
				queue.CompleteAdding();
			return;
		}

		if (!queue.IsAddingCompleted)
		{
			try
			{
				queue.Add(line);
			}
			catch (InvalidOperationException)
			{
				// Поток закрылся между проверкой и добавлением.
			}
		}
	}

	private static string? Take(BlockingCollection<string> queue) =>
		queue.TryTake(out string? line, Timeout.Infinite) ? line : null;
}

/// <summary>Члены <c>process_handle</c>; первый параметр — <c>self</c>.</summary>
[KernType("process_handle")]
public static class ProcessHandleMembers
{
	/// <summary><c>h.pid</c>: идентификатор процесса.</summary>
	[KernProperty]
	public static int Pid(KNativeBox self) => Require(self).Pid;

	/// <summary><c>h.wait() -&gt; i32</c>: дождаться завершения и вернуть код.</summary>
	[KernMember]
	public static int Wait(KNativeBox self) => Require(self).Wait();

	/// <summary><c>h.kill()</c>: завершить процесс; уже завершённый — не ошибка.</summary>
	[KernMember]
	public static void Kill(KNativeBox self) => Require(self).Kill();

	/// <summary><c>h.try_wait() -&gt; i32?</c>: код, если процесс уже завершился.</summary>
	[KernName("try_wait")]
	[KernMember]
	public static int? TryWait(KNativeBox self) => Require(self).TryWait();

	/// <summary><c>h.stdout_read_line() -&gt; str?</c>: строка вывода или <c>nil</c> в конце.</summary>
	[KernName("stdout_read_line")]
	[KernMember]
	public static string? StdoutReadLine(KNativeBox self) => Require(self).ReadStdoutLine();

	/// <summary><c>h.stderr_read_line() -&gt; str?</c>: строка ошибок или <c>nil</c> в конце.</summary>
	[KernName("stderr_read_line")]
	[KernMember]
	public static string? StderrReadLine(KNativeBox self) => Require(self).ReadStderrLine();

	/// <summary><c>h.write_stdin(text)</c>: записать в стандартный ввод.</summary>
	[KernName("write_stdin")]
	[KernMember]
	public static void WriteStdin(KNativeBox self, string text) => Require(self).WriteStdin(text);

	/// <summary><c>h.close_stdin()</c>: закрыть стандартный ввод.</summary>
	[KernName("close_stdin")]
	[KernMember]
	public static void CloseStdin(KNativeBox self) => Require(self).CloseStdin();

	private static KernProcessHandle Require(KNativeBox self) => self.Value switch
	{
		KernProcessHandle handle => handle,
		_ => throw new KernPanic($"process_handle: ожидался процесс, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>
/// <c>kern.process</c> (spec §16, раунд 27): запуск процессов. Ненулевой код возврата —
/// не ошибка; <c>Err</c> только если процесс не удалось запустить.
/// </summary>
[KernModule("kern.process")]
[KernCapability("process")]
public static class ProcessApi
{
	private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

	/// <summary><c>process.run(cmd, args) -&gt; result&lt;process_output, str&gt;</c>.</summary>
	[KernMember]
	public static KResult<KernProcessOutput, string> Run(string command, KList<string> arguments) =>
		RunCore(null, command, arguments);

	/// <summary><c>process.run_in(dir, cmd, args)</c>: запуск в указанном каталоге.</summary>
	[KernName("run_in")]
	[KernMember]
	public static KResult<KernProcessOutput, string> RunIn(
		string directory,
		string command,
		KList<string> arguments) =>
		RunCore(directory, command, arguments);

	/// <summary><c>process.shell(line)</c>: строка для командной оболочки платформы.</summary>
	[KernMember]
	public static KResult<KernProcessOutput, string> Shell(string line)
	{
		bool windows = OperatingSystem.IsWindows();
		string command = windows ? "cmd.exe" : "/bin/sh";
		return RunCore(null, command, Strings([windows ? "/c" : "-c", line]));
	}

	/// <summary><c>process.spawn(cmd, args) -&gt; process_handle</c>.</summary>
	[KernMember]
	public static KernProcessHandle Spawn(string command, KList<string> arguments)
	{
		try
		{
			var process = new Process { StartInfo = Info(null, command, arguments) };
			var handle = new KernProcessHandle(process);
			handle.Start();
			return handle;
		}
		catch (Exception error) when (error is Win32Exception
			or InvalidOperationException
			or ArgumentException
			or IOException)
		{
			throw new KernPanic($"process: не удалось запустить '{command}': {error.Message}", error);
		}
	}

	/// <summary><c>process.which(name) -&gt; str?</c>: полный путь к исполняемому файлу.</summary>
	[KernMember]
	public static string? Which(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return null;

		bool windows = OperatingSystem.IsWindows();
		string[] extensions = windows
			? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
				.Split(';', StringSplitOptions.RemoveEmptyEntries)
			: [string.Empty];
		IEnumerable<string> candidates;
		if (name.Contains('/') || name.Contains('\\'))
		{
			candidates = [name];
		}
		else
		{
			string? path = Environment.GetEnvironmentVariable("PATH");
			if (string.IsNullOrEmpty(path))
				return null;
			candidates = path
				.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
				.Select(directory => System.IO.Path.Combine(directory, name));
		}

		foreach (string candidate in candidates)
		{
			if (System.IO.File.Exists(candidate))
				return candidate;
			foreach (string extension in extensions)
			{
				string withExtension = candidate + extension;
				if (System.IO.File.Exists(withExtension))
					return withExtension;
			}
		}

		return null;
	}

	private static KResult<KernProcessOutput, string> RunCore(
		string? directory,
		string command,
		KList<string> arguments)
	{
		try
		{
			using var process = new Process { StartInfo = Info(directory, command, arguments) };
			process.Start();
			Task<string> stdout = process.StandardOutput.ReadToEndAsync();
			Task<string> stderr = process.StandardError.ReadToEndAsync();
			process.WaitForExit();
			Task.WaitAll(stdout, stderr);
			return KResult<KernProcessOutput, string>.Success(
				new KernProcessOutput(process.ExitCode, stdout.Result, stderr.Result));
		}
		catch (Exception error) when (error is Win32Exception
			or InvalidOperationException
			or ArgumentException
			or IOException)
		{
			return KResult<KernProcessOutput, string>.Failure(
				$"process: не удалось запустить '{command}': {error.Message}");
		}
	}

	private static ProcessStartInfo Info(string? directory, string command, KList<string> arguments)
	{
		var info = new ProcessStartInfo
		{
			FileName = command,
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			StandardOutputEncoding = Utf8,
			StandardErrorEncoding = Utf8,
		};
		if (!string.IsNullOrEmpty(directory))
			info.WorkingDirectory = directory;
		foreach (string argument in arguments)
			info.ArgumentList.Add(argument);
		return info;
	}

	private static KList<string> Strings(IEnumerable<string> items) =>
		new(KValue.Obj(new Kern.Vm.Values.KList(null, [.. items.Select(KValue.Str)])));
}
