using System.Text;
using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.IO;

/// <summary>
/// Потоки <c>kern.io</c> (spec §18c): <c>streamwriter</c> и <c>streamreader</c>.
/// <c>close()</c>/<c>dispose()</c> идемпотентны, файл всегда закрывается.
/// </summary>
/// <remarks>
/// Компилятор пока не умеет нативные имена типов (<c>NativeCatalog.Types</c> не попадает
/// в символы), поэтому члены зарегистрированы на нативном типе <c>object</c>, а фабрики
/// возвращают <c>object</c>; настоящий вид объекта проверяется при вызове. Когда появятся
/// символы нативных типов, этот bridge заменится на <c>[KernType("streamwriter")]</c> и
/// <c>[KernType("streamreader")]</c> без изменения поведения.
/// </remarks>
[KernType("object")]
public static class StreamApi
{
	private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

	/// <summary><c>write(text)</c>: записать строку без перевода строки.</summary>
	[KernName("write")]
	[KernMember]
	public static void Write(KNativeBox self, string text) => RequireWriter(self).Write(text);

	/// <summary><c>write_line()</c>: записать пустую строку.</summary>
	[KernName("write_line")]
	[KernMember]
	public static void WriteLine(KNativeBox self) => RequireWriter(self).WriteLine();

	/// <summary><c>write_line(text)</c>: записать строку с переводом строки.</summary>
	[KernName("write_line")]
	[KernMember]
	public static void WriteLine(KNativeBox self, string text) => RequireWriter(self).WriteLine(text);

	/// <summary><c>flush()</c>: сбросить буфер записи на диск.</summary>
	[KernMember]
	public static void Flush(KNativeBox self) => RequireWriter(self).Flush();

	/// <summary><c>read_line()</c>: следующая строка без перевода строки; в конце файла — <c>nil</c>.</summary>
	[KernName("read_line")]
	[KernMember]
	public static string? ReadLine(KNativeBox self) => RequireReader(self).ReadLine();

	/// <summary><c>read_all()</c>: остаток файла одной строкой (имя из examples/21).</summary>
	[KernName("read_all")]
	[KernMember]
	public static string ReadAll(KNativeBox self) => RequireReader(self).ReadText();

	/// <summary><c>read_text()</c>: остаток файла одной строкой (алиас <c>read_all()</c>).</summary>
	[KernName("read_text")]
	[KernMember]
	public static string ReadText(KNativeBox self) => RequireReader(self).ReadText();

	/// <summary><c>close()</c>: закрыть поток; повторный вызов ничего не делает.</summary>
	[KernMember]
	public static void Close(KNativeBox self) => RequireHandle(self).Close();

	/// <summary><c>dispose()</c>: закрыть поток; повторный вызов безопасен.</summary>
	[KernMember]
	public static void Dispose(KNativeBox self) => RequireHandle(self).Dispose();

	/// <summary><c>is_open</c>: поток ещё не закрыт.</summary>
	[KernName("is_open")]
	[KernProperty]
	public static bool IsOpen(KNativeBox self) => RequireHandle(self).IsOpen;

	/// <summary><c>at_end</c>: в <c>streamreader</c> не осталось данных.</summary>
	[KernName("at_end")]
	[KernProperty]
	public static bool AtEnd(KNativeBox self) => RequireReader(self).AtEnd;

	internal static UTF8Encoding Encoding => Utf8;

	/// <summary>Русская паника для ошибок открытия файла (вместо английского текста .NET).</summary>
	internal static KernPanic OpenError(string path, string mode, Exception exception) => exception switch
	{
		FileNotFoundException => new KernPanic($"файл '{path}' не найден"),
		DirectoryNotFoundException => new KernPanic($"папка для '{path}' не найдена"),
		UnauthorizedAccessException => new KernPanic($"нет доступа к '{path}' ({mode})"),
		ArgumentException => new KernPanic($"некорректный путь '{path}'"),
		_ => new KernPanic($"не удалось открыть '{path}' ({mode}): {exception.Message}"),
	};

	private static StreamHandle RequireHandle(KNativeBox self) => self.Value switch
	{
		StreamHandle handle => handle,
		_ => throw new KernPanic($"ожидался поток (streamwriter/streamreader), получено {self.Value.GetType().Name}"),
	};

	private static StreamWriterHandle RequireWriter(KNativeBox self) => RequireHandle(self) switch
	{
		StreamWriterHandle writer => writer,
		StreamHandle other => throw new KernPanic(
			$"операция записи применима к streamwriter, получено {other.KindName}"),
	};

	private static StreamReaderHandle RequireReader(KNativeBox self) => RequireHandle(self) switch
	{
		StreamReaderHandle reader => reader,
		StreamHandle other => throw new KernPanic(
			$"операция чтения применима к streamreader, получено {other.KindName}"),
	};
}

/// <summary>Общее состояние потоков: закрытие идемпотентно.</summary>
[KernType("stream")]
public abstract class StreamHandle : IDisposable
{
	private bool _closed;

	public bool IsOpen => !_closed;

	public abstract string KindName { get; }

	public void Close()
	{
		if (_closed)
			return;
		_closed = true;
		DisposeCore();
	}

	public void Dispose() => Close();

	protected abstract void DisposeCore();

	protected void EnsureOpen()
	{
		if (_closed)
			throw new KernPanic($"поток {KindName} уже закрыт");
	}
}

/// <summary>Поток записи UTF-8 без BOM.</summary>
[KernType("streamwriter")]
public sealed class StreamWriterHandle : StreamHandle
{
	private readonly StreamWriter _writer;

	public StreamWriterHandle(string path) =>
		_writer = new StreamWriter(path, append: false, StreamApi.Encoding);

	public override string KindName => "streamwriter";

	public void Write(string text)
	{
		EnsureOpen();
		_writer.Write(text);
	}

	public void WriteLine()
	{
		EnsureOpen();
		_writer.WriteLine();
	}

	public void WriteLine(string text)
	{
		EnsureOpen();
		_writer.WriteLine(text);
	}

	public void Flush()
	{
		EnsureOpen();
		_writer.Flush();
	}

	protected override void DisposeCore() => _writer.Dispose();
}

/// <summary>Поток чтения UTF-8.</summary>
[KernType("streamreader")]
public sealed class StreamReaderHandle : StreamHandle
{
	private readonly StreamReader _reader;

	public StreamReaderHandle(string path) => _reader = new StreamReader(path, StreamApi.Encoding);

	public override string KindName => "streamreader";

	public string? ReadLine()
	{
		EnsureOpen();
		return _reader.ReadLine();
	}

	public string ReadText()
	{
		EnsureOpen();
		return _reader.ReadToEnd();
	}

	public bool AtEnd
	{
		get
		{
			EnsureOpen();
			return _reader.EndOfStream;
		}
	}

	protected override void DisposeCore() => _reader.Dispose();
}

/// <summary>Фабрики потоков в модуле <c>kern.io</c> (spec §18c).</summary>
public static partial class ConsoleModule
{
	/// <summary><c>streamwriter(path)</c>: открыть файл на запись (перезапись), UTF-8 без BOM.</summary>
	[KernName("streamwriter")]
	[KernMember]
	public static StreamWriterHandle OpenWriter(string path)
	{
		try
		{
			return new StreamWriterHandle(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			throw StreamApi.OpenError(path, "запись", exception);
		}
	}

	/// <summary><c>streamreader(path)</c>: открыть файл на чтение, UTF-8.</summary>
	[KernName("streamreader")]
	[KernMember]
	public static StreamReaderHandle OpenReader(string path)
	{
		try
		{
			return new StreamReaderHandle(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			throw StreamApi.OpenError(path, "чтение", exception);
		}
	}
}
