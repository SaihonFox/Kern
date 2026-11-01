namespace Kern.Cli.Build;

/// <summary>Исходник, попавший в цель: путь модуля (для <c>use</c>) и настоящий путь файла.</summary>
internal sealed record BuildSourceFile(string ModulePath, string FullPath, string Text);

/// <summary>
/// Набор исходников цели (docs/BUILDER.md §3): её <c>src</c> плюс <c>src</c> всех зависимостей
/// по исходникам. Путь модуля считается от корня источника цели, поэтому <c>use util</c>
/// находит <c>util.kern</c> и в своей цели, и в зависимости.
/// </summary>
internal static class BuildTargetSources
{
	public static IReadOnlyList<BuildSourceFile> Collect(BuildSession session, TargetModel target)
	{
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(target);

		IReadOnlyList<TargetModel> closure = BuildGraph.Closure(session.Workspace, target);
		var files = new List<BuildSourceFile>();
		var byFullPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var byModulePath = new Dictionary<string, BuildSourceFile>(StringComparer.Ordinal);

		foreach (TargetModel current in closure)
		{
			foreach (string source in current.Src)
			{
				if (string.IsNullOrWhiteSpace(source))
					throw new BuildException($"цель '{current.Name}': пустой путь в src");

				string path = Resolve(session.Root, current, source);
				if (File.Exists(path))
				{
					Add(current, path, Path.GetFileName(path), files, byFullPath, byModulePath);
					continue;
				}

				if (!Directory.Exists(path))
					throw new BuildException($"цель '{current.Name}': источник не найден: {source}");

				string[] found;
				try
				{
					found = [.. Directory.EnumerateFiles(path, "*.kern", SearchOption.AllDirectories)];
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					throw new BuildException($"цель '{current.Name}': не удалось прочитать {source}: {ex.Message}");
				}

				Array.Sort(found, StringComparer.Ordinal);
				foreach (string file in found)
				{
					string modulePath = Path.GetRelativePath(path, file).Replace('\\', '/');
					Add(current, file, modulePath, files, byFullPath, byModulePath);
				}
			}

			foreach (string dependency in current.Deps)
			{
				if (!dependency.Contains(':', StringComparison.Ordinal))
					continue;
				int split = dependency.LastIndexOf(':');
				string project = dependency[..split];
				string targetName = dependency[(split + 1)..];
				BuildSession included = session.FindIncluded(project)
					?? throw new BuildException(
						$"цель '{current.Name}': подпроект '{project}' не найден среди w.include");
				TargetModel external = included.Workspace.FindTarget(targetName)
					?? throw new BuildException(
						$"цель '{current.Name}': в подпроекте '{project}' нет цели '{targetName}'");
				foreach (BuildSourceFile file in Collect(included, external))
					Add(current, file.FullPath, file.ModulePath, files, byFullPath, byModulePath);
			}
		}

		if (files.Count == 0)
			throw new BuildException($"у цели '{target.Name}' нет исходников .kern");

		files.Sort((left, right) => string.CompareOrdinal(left.ModulePath, right.ModulePath));
		return files;
	}

	private static void Add(
		TargetModel target,
		string fullPath,
		string modulePath,
		List<BuildSourceFile> files,
		HashSet<string> byFullPath,
		Dictionary<string, BuildSourceFile> byModulePath)
	{
		if (!fullPath.EndsWith(".kern", StringComparison.OrdinalIgnoreCase))
			throw new BuildException($"цель '{target.Name}': исходник не .kern: {fullPath}");
		if (!byFullPath.Add(fullPath))
			return;

		string text;
		try
		{
			text = File.ReadAllText(fullPath);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new BuildException($"не удалось прочитать {fullPath}: {ex.Message}");
		}

		var file = new BuildSourceFile(modulePath, fullPath, text);
		if (byModulePath.TryGetValue(modulePath, out BuildSourceFile? existing))
			throw new BuildException(
				$"цель '{target.Name}': модуль '{modulePath}' задан двумя файлами: {existing.FullPath} и {fullPath}");

		byModulePath[modulePath] = file;
		files.Add(file);
	}

	/// <summary>
	/// Путь исходников: относительный <c>src</c> ищется сначала от папки цели
	/// (<c>&lt;root&gt;/&lt;цель&gt;/src</c> — так устроен пример 11 и прежний builder),
	/// затем от корня проекта (<c>&lt;root&gt;/src</c>, docs/BUILDER.md §3).
	/// </summary>
	private static string Resolve(string root, TargetModel target, string source)
	{
		try
		{
			if (Path.IsPathRooted(source))
				return Path.GetFullPath(source);

			string fromTarget = Path.GetFullPath(Path.Combine(root, target.Name, source));
			if (File.Exists(fromTarget) || Directory.Exists(fromTarget))
				return fromTarget;

			return Path.GetFullPath(Path.Combine(root, source));
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new BuildException($"неверный путь источника: {source}");
		}
	}
}
