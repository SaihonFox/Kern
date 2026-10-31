using System.Text;
using Kern.Binding.Attributes;

namespace Kern.Runtime.IO;

/// <summary>Базовый тип потоков: `use stream s = …` закрывает любой из них (наследники: streamwriter, streamreader).</summary>
[KernType("stream")]
public abstract class KernStream : IDisposable
{
	[KernMember] public abstract void Close();
	[KernMember] public virtual void Dispose() => Close();
}

/// <summary>Потоковая запись текста в файл (UTF-8 без BOM). `use streamwriter sw = streamwriter("a.txt")` закрывает файл при выходе из блока.</summary>
[KernType("streamwriter")]
public sealed class KernStreamWriter : KernStream
{
	private readonly StreamWriter _w;

	[KernCtor] public KernStreamWriter(string path, bool append = false) =>
		_w = new StreamWriter(path, append, new UTF8Encoding(false));

	[KernMember] public void Write(string text) => _w.Write(text);
	[KernMember] public void WriteLine(string text = "") => _w.WriteLine(text);
	[KernMember] public void Flush() => _w.Flush();
	[KernMember] public override void Close() => _w.Dispose();
	[KernMember] public override void Dispose() => _w.Dispose();
}

/// <summary>Потоковое чтение текста из файла (UTF-8).</summary>
[KernType("streamreader")]
public sealed class KernStreamReader : KernStream
{
	private readonly StreamReader _r;

	[KernCtor] public KernStreamReader(string path) => _r = new StreamReader(path, new UTF8Encoding(false));

	/// <summary>Следующая строка или nil в конце файла.</summary>
	[KernMember] public string? ReadLine() => _r.ReadLine();
	[KernMember] public string ReadAll() => _r.ReadToEnd();
	[KernProperty] public bool AtEnd => _r.EndOfStream;
	[KernMember] public override void Close() => _r.Dispose();
	[KernMember] public override void Dispose() => _r.Dispose();
}
