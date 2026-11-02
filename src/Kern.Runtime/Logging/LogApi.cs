using System.Globalization;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Bytecode;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Logging;

/// <summary>
/// <c>kern.log</c> (spec §16, раунд 27): глобальный логгер процесса.
/// Уровень по умолчанию — <c>info</c>, вывод — в <c>stderr</c>. Строка имеет вид
/// <c>2026-11-02T12:34:56.789Z INFO  текст</c>, сообщения ниже уровня отбрасываются.
/// Запись защищена одним замком: логгер общий для всех потоков процесса.
/// </summary>
[KernModule("kern.log")]
public static class LogApi
{
	private enum LogLevel
	{
		Trace,
		Debug,
		Info,
		Warn,
		Error,
		Off,
	}

	private enum LogTarget
	{
		Stderr,
		Stdout,
		File,
	}

	private static readonly Lock Gate = new();
	private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
	private static LogLevel _level = LogLevel.Info;
	private static LogTarget _target = LogTarget.Stderr;
	private static StreamWriter? _file;

	/// <summary><c>log.level</c>: текущий уровень (<c>trace|debug|info|warn|error|off</c>).</summary>
	[KernProperty]
	public static string Level()
	{
		lock (Gate)
			return Name(_level);
	}

	/// <summary>Сеттер свойства <c>log.level</c> (spec §16).</summary>
	[KernName("level")]
	[KernProperty]
	public static void SetLevelProperty(string value)
	{
		lock (Gate)
			_level = Parse(value);
	}

	/// <summary><c>log.set_level(s)</c>: сменить уровень; неизвестный — паника.</summary>
	[KernName("set_level")]
	[KernMember]
	public static void SetLevel(string value)
	{
		lock (Gate)
			_level = Parse(value);
	}

	/// <summary><c>log.to_stderr()</c>: писать в поток ошибок (поведение по умолчанию).</summary>
	[KernName("to_stderr")]
	[KernMember]
	public static void ToStderr()
	{
		lock (Gate)
		{
			CloseFile();
			_target = LogTarget.Stderr;
		}
	}

	/// <summary><c>log.to_stdout()</c>: писать в стандартный вывод.</summary>
	[KernName("to_stdout")]
	[KernMember]
	public static void ToStdout()
	{
		lock (Gate)
		{
			CloseFile();
			_target = LogTarget.Stdout;
		}
	}

	/// <summary>
	/// <c>log.to_file(path) -&gt; result&lt;void, str&gt;</c>: дозапись в файл (создаётся при
	/// отсутствии). Ошибка открытия — <c>Err</c> с русским текстом, а не паника.
	/// </summary>
	[KernName("to_file")]
	[KernMember]
	[KernReturn("result<void, str>")]
	public static KValue ToFile(string path, Interpreter interpreter)
	{
		lock (Gate)
		{
			try
			{
				CloseFile();
				var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
				_file = new StreamWriter(stream, Utf8) { AutoFlush = true };
				_target = LogTarget.File;
				return KernResults.Ok(interpreter, interpreter.Types.Prim(PrimKind.Void), KValue.Void);
			}
			catch (Exception error) when (error is IOException
				or UnauthorizedAccessException
				or ArgumentException
				or NotSupportedException)
			{
				_target = LogTarget.Stderr;
				return KernResults.Err(
					interpreter,
					interpreter.Types.Prim(PrimKind.Str),
					KValue.Str($"log: не удалось открыть '{path}': {error.Message}"));
			}
		}
	}

	/// <summary><c>log.close()</c>: закрыть файл; вывод возвращается в <c>stderr</c>.</summary>
	[KernMember]
	public static void Close()
	{
		lock (Gate)
		{
			CloseFile();
			_target = LogTarget.Stderr;
		}
	}

	/// <summary><c>log.log(level, msg)</c>: записать сообщение с явным уровнем.</summary>
	[KernMember]
	public static void Log(string level, string message)
	{
		lock (Gate)
			Write(Parse(level), message);
	}

	/// <summary><c>log.trace(msg)</c>.</summary>
	[KernMember]
	public static void Trace(string message)
	{
		lock (Gate)
			Write(LogLevel.Trace, message);
	}

	/// <summary><c>log.debug(msg)</c>.</summary>
	[KernMember]
	public static void Debug(string message)
	{
		lock (Gate)
			Write(LogLevel.Debug, message);
	}

	/// <summary><c>log.info(msg)</c>.</summary>
	[KernMember]
	public static void Info(string message)
	{
		lock (Gate)
			Write(LogLevel.Info, message);
	}

	/// <summary><c>log.warn(msg)</c>.</summary>
	[KernMember]
	public static void Warn(string message)
	{
		lock (Gate)
			Write(LogLevel.Warn, message);
	}

	/// <summary><c>log.error(msg)</c>.</summary>
	[KernMember]
	public static void Error(string message)
	{
		lock (Gate)
			Write(LogLevel.Error, message);
	}

	private static void Write(LogLevel level, string message)
	{
		if (_level == LogLevel.Off || level < _level)
			return;

		string stamp = DateTime.UtcNow.ToString(
			"yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
			CultureInfo.InvariantCulture);
		string line = $"{stamp} {Name(level).ToUpperInvariant().PadRight(5)} {message}";

		switch (_target)
		{
			case LogTarget.File when _file is not null:
				_file.WriteLine(line);
				break;
			case LogTarget.Stdout:
				Console.Out.WriteLine(line);
				break;
			default:
				Console.Error.WriteLine(line);
				break;
		}
	}

	private static LogLevel Parse(string value)
	{
		return value.Trim().ToLowerInvariant() switch
		{
			"trace" => LogLevel.Trace,
			"debug" => LogLevel.Debug,
			"info" => LogLevel.Info,
			"warn" => LogLevel.Warn,
			"error" => LogLevel.Error,
			"off" => LogLevel.Off,
			_ => throw new KernPanic(
				$"log: неизвестный уровень '{value}' (ожидалось trace|debug|info|warn|error|off)"),
		};
	}

	private static string Name(LogLevel level) => level switch
	{
		LogLevel.Trace => "trace",
		LogLevel.Debug => "debug",
		LogLevel.Info => "info",
		LogLevel.Warn => "warn",
		LogLevel.Error => "error",
		_ => "off",
	};

	private static void CloseFile()
	{
		if (_file is null)
			return;
		_file.Dispose();
		_file = null;
	}
}
