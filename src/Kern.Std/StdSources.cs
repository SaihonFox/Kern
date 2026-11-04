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
		@"^\s*use\s+kern\.(?<name>[a-z_][a-z0-9_]*)\s*(?:\[|\.\*)",
		RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

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

	private static readonly HashSet<string> Auto = new(StringComparer.Ordinal) { "ansi", "csv", "parse" };

	/// <summary>
	/// Добавить std-модуль, если проект использует <c>use kern.&lt;name&gt;</c>. Уже
	/// присутствующий в проекте файл с тем же именем не дублируется.
	/// </summary>
	public static IReadOnlyList<(string Path, string Text)> Include(
		IReadOnlyList<(string Path, string Text)> files)
	{
		ArgumentNullException.ThrowIfNull(files);
		var names = new HashSet<string>(StringComparer.Ordinal);
		var defined = new HashSet<string>(StringComparer.Ordinal);
		foreach (var (path, text) in files)
		{
			Match project = Project.Match(text);
			defined.Add(project.Success
				? project.Groups["name"].Value
				: Path.GetFileNameWithoutExtension(path));
			foreach (Match match in Use.Matches(text))
				names.Add(match.Groups["name"].Value);
		}

		if (names.Count == 0)
			return files;

		var result = new List<(string Path, string Text)>(files);
		var paths = new HashSet<string>(files.Select(file => file.Path), StringComparer.OrdinalIgnoreCase);
		foreach (var (path, text) in Load())
		{
			string name = Path.GetFileNameWithoutExtension(path);
			if (!names.Contains(name) || !Auto.Contains(name) || defined.Contains(name) || defined.Contains("kern." + name) || paths.Contains(path))
				continue;
			result.Add((path, text));
			paths.Add(path);
			defined.Add(name);
		}

		return result;
	}

	private static readonly Regex Project = new(
		@"^\s*project\s+(?<name>[a-zA-Z_][a-zA-Z0-9_.]*)\s*:",
		RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
}
