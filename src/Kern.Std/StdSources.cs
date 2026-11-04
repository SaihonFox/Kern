using System.Reflection;
using System.Text.RegularExpressions;

namespace Kern.Std;

/// <summary>
/// Встроенные исходники <c>Kern.Std/std/**/*.kern</c>. <c>kern.ansi</c> подключается
/// автоматически при <c>use kern.ansi…</c>; <c>kern.csv</c> пока компилируется отдельно,
/// потому что одноимённый C#-модуль ещё не удалён.
/// </summary>
public static class StdSources
{
	private const string Prefix = "std/";

	private static readonly Regex Use = new(
		@"\buse\s+kern\.(?<name>[a-z_][a-z0-9_]*)\s*(?:\[|\.\*)",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public static IReadOnlyList<(string Path, string Text)> Load()
	{
		Assembly assembly = typeof(StdSources).Assembly;
		var files = new List<(string Path, string Text)>();
		foreach (string resource in assembly.GetManifestResourceNames())
		{
			if (!resource.StartsWith(Prefix, StringComparison.Ordinal))
				continue;
			using Stream stream = assembly.GetManifestResourceStream(resource)
				?? throw new InvalidOperationException($"нет ресурса '{resource}'");
			using var reader = new StreamReader(stream);
			files.Add((resource, reader.ReadToEnd()));
		}

		files.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
		return files;
	}

	private static readonly HashSet<string> Auto = new(StringComparer.Ordinal) { "ansi" };

	/// <summary>
	/// Добавить std-модуль, если проект использует <c>use kern.&lt;name&gt;</c>. Уже
	/// присутствующий в проекте файл с тем же именем не дублируется.
	/// </summary>
	public static IReadOnlyList<(string Path, string Text)> Include(
		IReadOnlyList<(string Path, string Text)> files)
	{
		ArgumentNullException.ThrowIfNull(files);
		var names = new HashSet<string>(StringComparer.Ordinal);
		foreach (var (_, text) in files)
			foreach (Match match in Use.Matches(text))
				names.Add(match.Groups["name"].Value);

		if (names.Count == 0)
			return files;

		var result = new List<(string Path, string Text)>(files);
		var paths = new HashSet<string>(files.Select(file => file.Path), StringComparer.OrdinalIgnoreCase);
		foreach (var (path, text) in Load())
		{
			string name = Path.GetFileNameWithoutExtension(path);
			if (!names.Contains(name) || !Auto.Contains(name) || paths.Contains(path))
				continue;
			result.Add((path, text));
			paths.Add(path);
		}

		return result;
	}
}
