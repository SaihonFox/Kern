using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Values;

namespace Kern.Runtime.IO;

/// <summary>Работа с файлами (UTF-8 без BOM). Ошибки ввода-вывода возвращаются как result; *_async — настоящие async-операции.</summary>
[KernType("file")]
public static class FileApi
{
	private static readonly UTF8Encoding Utf8 = new(false);

	private static KResult<T, string> Try<T>(Func<KValue> body)
	{
		try { return KResult<T, string>.Ok(body()); }
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return Kv.Err<T>(e.Message);
		}
	}

	[KernStatic] public static bool Exists(string path) => File.Exists(path);

	[KernStatic] public static KResult<string, string> ReadText(string path) => Try<string>(() => KValue.Str(File.ReadAllText(path, Utf8)));
	[KernStatic] public static KResult<KList<string>, string> ReadLines(string path) =>
		Try<KList<string>>(() => KValue.Obj(Kv.Strs(File.ReadAllLines(path, Utf8)).Inner));
	[KernStatic] public static KResult<bool, string> WriteText(string path, string text) =>
		Try<bool>(() => { File.WriteAllText(path, text, Utf8); return KValue.True; });
	[KernStatic] public static KResult<bool, string> AppendText(string path, string text) =>
		Try<bool>(() => { File.AppendAllText(path, text, Utf8); return KValue.True; });
	[KernStatic] public static KResult<bool, string> Delete(string path) =>
		Try<bool>(() => { File.Delete(path); return KValue.True; });
	[KernStatic] public static KResult<bool, string> Copy(string from, string to, bool overwrite = false) =>
		Try<bool>(() => { File.Copy(from, to, overwrite); return KValue.True; });
	[KernStatic] public static KResult<bool, string> Move(string from, string to) =>
		Try<bool>(() => { File.Move(from, to, overwrite: true); return KValue.True; });
	[KernStatic] public static KResult<long, string> Size(string path) => Try<long>(() => KValue.I64(new FileInfo(path).Length));

	// ---------- async ----------
	[KernStatic] public static async Task<string> ReadTextAsync(string path) => await File.ReadAllTextAsync(path, Utf8);
	[KernStatic] public static async Task WriteTextAsync(string path, string text) => await File.WriteAllTextAsync(path, text, Utf8);
	[KernStatic] public static async Task AppendTextAsync(string path, string text) => await File.AppendAllTextAsync(path, text, Utf8);
}

/// <summary>Пути: <c>path.join("a", "b")</c>.</summary>
[KernType("path")]
public static class PathApi
{
	[KernStatic] public static string Join(string a, string b) => Path.Combine(a, b);
	[KernStatic] public static string Join(string a, string b, string c) => Path.Combine(a, b, c);
	[KernStatic] public static string FileName(string path) => Path.GetFileName(path);
	[KernStatic] public static string Stem(string path) => Path.GetFileNameWithoutExtension(path);
	[KernStatic] public static string Extension(string path) => Path.GetExtension(path);
	[KernStatic] public static string? Parent(string path) => Path.GetDirectoryName(path);
	[KernStatic] public static string Full(string path) => Path.GetFullPath(path);
	[KernStatic] public static bool IsAbsolute(string path) => Path.IsPathRooted(path);
	[KernStatic] public static string Separator() => Path.DirectorySeparatorChar.ToString();
}

/// <summary>Каталоги.</summary>
[KernType("dir")]
public static class DirApi
{
	[KernStatic] public static bool Exists(string path) => Directory.Exists(path);
	[KernStatic] public static KResult<bool, string> Create(string path)
	{
		try { Directory.CreateDirectory(path); return KResult<bool, string>.Ok(KValue.True); }
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return Kv.Err<bool>(e.Message); }
	}
	[KernStatic] public static KResult<bool, string> Delete(string path, bool recursive = false)
	{
		try { Directory.Delete(path, recursive); return KResult<bool, string>.Ok(KValue.True); }
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return Kv.Err<bool>(e.Message); }
	}
	[KernStatic] public static KList<string> Files(string path, string pattern = "*") =>
		Kv.Strs(Directory.Exists(path) ? Directory.EnumerateFiles(path, pattern) : []);
	[KernStatic] public static KList<string> Dirs(string path) =>
		Kv.Strs(Directory.Exists(path) ? Directory.EnumerateDirectories(path) : []);
}
