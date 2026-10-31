namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc clean &lt;папка&gt;</c>: удаляет каталоги результатов проекта — <c>out/</c> и
/// <c>dist/</c> — рекурсивно. Отсутствие каталогов не ошибка. Печатает, что удалено.
/// Не папка (или папки нет) — код возврата 2.
/// </summary>
internal static class CleanCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		if (args.Length != 1 || args[0].StartsWith('-'))
		{
			stderr.WriteLine("ошибка: укажите папку проекта: kernc clean <папка>");
			return 2;
		}

		string directory = args[0];
		if (!Directory.Exists(directory))
		{
			stderr.WriteLine($"ошибка: папка не найдена: {directory}");
			return 2;
		}

		string root;
		try
		{
			root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			stderr.WriteLine($"ошибка: неверный путь: {directory}");
			return 2;
		}

		bool removed = false;
		foreach (string name in new[] { "out", "dist" })
		{
			string target = Path.Combine(root, name);
			if (!Directory.Exists(target))
				continue;

			try
			{
				Directory.Delete(target, recursive: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				stderr.WriteLine($"ошибка: не удалось удалить {target}: {ex.Message}");
				return 1;
			}

			stdout.WriteLine($"удалено: {target}");
			removed = true;
		}

		if (!removed)
			stdout.WriteLine($"нечего удалять: {root}");
		return 0;
	}
}
