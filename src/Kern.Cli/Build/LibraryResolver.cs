using System.Reflection;

namespace Kern.Cli.Build;

/// <summary>
/// Поиск внешних библиотек <c>Kern.Lib.*.dll</c> (docs/BUILDER.md §6): переменная
/// окружения <c>KERN_LIBS</c>, <c>libs</c> рядом с kernc, <c>~/.kern/libs</c>, <c>libs</c>
/// проекта и явные <c>lib_path</c>. Библиотека подключается по имени модуля
/// (<c>t.lib("kern.db")</c>).
/// </summary>
internal static class LibraryResolver
{
	private static readonly Dictionary<string, Assembly> Cache = new(StringComparer.OrdinalIgnoreCase);
	private static readonly Lock Gate = new();

	public static IReadOnlyList<Assembly> Resolve(BuildSession session, TargetModel target)
	{
		if (target.Libs.Count == 0)
			return [];

		var roots = SearchRoots(session, target);
		var assemblies = new List<Assembly>(target.Libs.Count);
		foreach (string library in target.Libs)
		{
			string path = Find(library, roots)
				?? throw new BuildException(
					$"библиотека '{library}' не найдена; искали в: {string.Join(", ", roots)}");
			lock (Gate)
			{
				if (!Cache.TryGetValue(path, out Assembly? assembly))
				{
					try
					{
						assembly = Assembly.LoadFrom(path);
					}
					catch (Exception exception) when (exception is IOException or BadImageFormatException)
					{
						throw new BuildException($"не удалось загрузить библиотеку {path}: {exception.Message}");
					}

					Cache[path] = assembly;
				}

				assemblies.Add(assembly);
			}
		}

		return assemblies;
	}

	private static IReadOnlyList<string> SearchRoots(BuildSession session, TargetModel target)
	{
		var roots = new List<string>();
		string? environment = Environment.GetEnvironmentVariable("KERN_LIBS");
		if (!string.IsNullOrWhiteSpace(environment))
			roots.AddRange(environment.Split(
				OperatingSystem.IsWindows() ? ';' : ':',
				StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

		string baseDirectory = AppContext.BaseDirectory;
		roots.Add(Path.Combine(baseDirectory, "libs"));
		for (DirectoryInfo? directory = new(baseDirectory); directory is not null; directory = directory.Parent)
		{
			roots.Add(Path.Combine(directory.FullName, "libs"));
			roots.Add(Path.Combine(directory.FullName, "libs", "_out"));
		}

		roots.Add(Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kern", "libs"));
		roots.Add(Path.Combine(session.Root, "libs"));
		foreach (string path in session.Workspace.LibPaths.Concat(target.LibPaths))
			roots.Add(Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(session.Root, path)));
		return [.. roots.Distinct(StringComparer.OrdinalIgnoreCase)];
	}

	private static string? Find(string library, IReadOnlyList<string> roots)
	{
		string shortName = library.Split('.').Last();
		string decorated = char.ToUpperInvariant(shortName[0]) + shortName[1..];
		string[] candidates =
		[
			$"Kern.Lib.{decorated}.dll",
			$"Kern.Lib.{shortName}.dll",
			$"{shortName}.dll",
		];
		foreach (string root in roots)
		{
			if (File.Exists(root))
			{
				if (candidates.Contains(Path.GetFileName(root), StringComparer.OrdinalIgnoreCase))
					return root;
				continue;
			}

			if (!Directory.Exists(root))
				continue;
			foreach (string candidate in candidates)
			{
				string direct = Path.Combine(root, candidate);
				if (File.Exists(direct))
					return direct;
			}

			try
			{
				foreach (string directory in Directory.EnumerateDirectories(root))
					foreach (string candidate in candidates)
					{
						string nested = Path.Combine(directory, candidate);
						if (File.Exists(nested))
							return nested;
					}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Нечитаемый корень поиска просто пропускаем; ошибка будет ниже, если DLL нет.
			}
		}

		return null;
	}
}
