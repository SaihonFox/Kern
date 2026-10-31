namespace Kern.Cli.Infrastructure;

/// <summary>
/// Источники проекта по соглашению (docs/BUILDER.md §2): <c>&lt;папка&gt;/src/**/*.kern</c>,
/// а если папки <c>src/</c> нет — <c>&lt;папка&gt;/*.kern</c> без рекурсии. Пути файлов —
/// относительно папки проекта, разделители <c>/</c>, порядок — детерминированный по пути.
/// Имя проекта — имя папки; оно же имя модуля и имя файла <c>out/&lt;имя&gt;.kbc</c>.
/// </summary>
internal sealed record ProjectSources(string Name, string Root, IReadOnlyList<(string Path, string Text)> Files)
{
	/// <summary>
	/// Читает проект. <paramref name="directory"/> уже должен существовать (это проверяет
	/// разбор аргументов); ошибки чтения возвращаются строкой для stderr.
	/// </summary>
	public static bool TryLoad(string directory, out ProjectSources? project, out string? error)
	{
		project = null;
		error = null;

		string root;
		try
		{
			root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			error = $"неверный путь проекта: {directory}";
			return false;
		}

		string name = new DirectoryInfo(root).Name;
		if (string.IsNullOrEmpty(name))
			name = "main";

		string sourceDirectory = Path.Combine(root, "src");
		bool nested = Directory.Exists(sourceDirectory);
		string searchRoot = nested ? sourceDirectory : root;
		SearchOption option = nested ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

		string[] paths;
		try
		{
			paths = [.. Directory.EnumerateFiles(searchRoot, "*.kern", option)];
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			error = $"не удалось прочитать папку проекта {directory}: {ex.Message}";
			return false;
		}

		if (paths.Length == 0)
		{
			error = nested
				? $"в папке {Path.Combine(directory, "src")} нет файлов .kern"
				: $"в папке {directory} нет файлов .kern";
			return false;
		}

		List<(string Path, string Text)> files = [];
		foreach (string file in paths)
		{
			string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
			string text;
			try
			{
				text = File.ReadAllText(file);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				error = $"не удалось прочитать {relative}: {ex.Message}";
				return false;
			}

			files.Add((relative, text));
		}

		files.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
		project = new ProjectSources(name, root, files);
		return true;
	}

	/// <summary>Путь результата <c>build</c>: явный <c>-o</c> или <c>&lt;root&gt;/out/&lt;name&gt;.kbc</c>.</summary>
	public string OutputKbcPath(string? output) =>
		output ?? Path.Combine(Root, "out", Name + ".kbc");
}
