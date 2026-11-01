using Kern.Binding.Attributes;

namespace Kern.Runtime.IO;

/// <summary>
/// <c>kern.path</c> (spec §16, раунд 27): чисто лексические операции над путями.
/// Файловую систему не трогают, поэтому возвращают обычные значения и не паникуют.
/// </summary>
[KernModule("kern.path")]
public static class PathApi
{
	/// <summary><c>path.join(a, b)</c>: склеить два сегмента с разделителем платформы.</summary>
	[KernMember]
	public static string Join(string first, string second) => System.IO.Path.Combine(first, second);

	/// <summary><c>path.join(a, b, c)</c>: склеить три сегмента.</summary>
	[KernMember]
	public static string Join3(string first, string second, string third) =>
		System.IO.Path.Combine(first, second, third);

	/// <summary><c>path.dir_name(p)</c>: каталог пути; у корня/пустого — пустая строка.</summary>
	[KernMember]
	public static string DirName(string path) => System.IO.Path.GetDirectoryName(path) ?? string.Empty;

	/// <summary><c>path.file_name(p)</c>: имя файла с расширением.</summary>
	[KernMember]
	public static string FileName(string path) => System.IO.Path.GetFileName(path);

	/// <summary><c>path.ext(p)</c>: расширение вместе с точкой (<c>.txt</c>), иначе пусто.</summary>
	[KernMember]
	public static string Ext(string path) => System.IO.Path.GetExtension(path);

	/// <summary><c>path.is_absolute(p)</c>: абсолютный ли путь.</summary>
	[KernMember]
	public static bool IsAbsolute(string path) => System.IO.Path.IsPathFullyQualified(path);

	/// <summary><c>path.absolute(p)</c>: абсолютная форма относительно текущего каталога.</summary>
	[KernMember]
	public static string Absolute(string path) => System.IO.Path.GetFullPath(path);

	/// <summary>
	/// <c>path.normalize(p)</c>: убрать <c>.</c>/<c>..</c> и лишние разделители, не делая путь
	/// абсолютным (для абсолютного входа результат тоже лексически канонический).
	/// </summary>
	[KernMember]
	public static string Normalize(string path) =>
		System.IO.Path.IsPathFullyQualified(path)
			? System.IO.Path.GetFullPath(path)
			: NormalizeRelative(path);

	private static string NormalizeRelative(string path)
	{
		if (string.IsNullOrEmpty(path))
			return path;

		bool rooted = path[0] is '/' or '\\';
		var parts = new List<string>();
		foreach (string segment in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
		{
			if (segment == ".")
				continue;
			if (segment == ".." && parts.Count > 0 && parts[^1] != "..")
			{
				parts.RemoveAt(parts.Count - 1);
				continue;
			}

			parts.Add(segment);
		}

		string joined = string.Join(System.IO.Path.DirectorySeparatorChar, parts);
		return rooted ? System.IO.Path.DirectorySeparatorChar + joined : joined;
	}
}
