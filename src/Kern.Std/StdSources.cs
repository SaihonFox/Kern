using System.Reflection;

namespace Kern.Std;

/// <summary>
/// Встроенные исходники <c>Kern.Std/std/**/*.kern</c>. Пока это пилот: модули
/// <c>kern.ansi</c>/<c>kern.csv</c> компилируются как обычные Kern-проекты и могут быть
/// подключены через <c>use</c>; C#-версии остаются для совместимости.
/// </summary>
public static class StdSources
{
	private const string Prefix = "std/";

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
}
