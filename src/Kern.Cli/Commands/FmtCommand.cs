using System.Text;
using Kern.Compiler.Formatting;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc fmt [путь…] [--check] [--stdout]</c> (spec §16, раунд 6/11): табы и CRLF,
/// без хвостовых пробелов и лишних пустых строк. <c>--check</c> только сообщает о различиях
/// (код 1), <c>--stdout</c> печатает результат и не пишет файл.
/// </summary>
internal static class FmtCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		bool check = args.Contains("--check");
		bool toStdout = args.Contains("--stdout");
		var paths = new List<string>();
		foreach (string argument in args)
		{
			if (argument.StartsWith('-'))
			{
				if (argument is not ("--check" or "--stdout"))
				{
					stderr.WriteLine($"ошибка: неизвестный параметр '{argument}'");
					return 2;
				}

				continue;
			}

			paths.Add(argument);
		}

		if (paths.Count == 0)
			paths.Add(".");

		List<string> files = Collect(paths, stderr, out bool failed);
		if (failed)
			return 1;
		if (files.Count == 0)
		{
			stderr.WriteLine("ошибка: не найдено файлов .kern");
			return 1;
		}

		if (toStdout && files.Count != 1)
		{
			stderr.WriteLine("ошибка: --stdout принимает ровно один файл");
			return 2;
		}

		var changed = 0;
		foreach (string file in files)
		{
			string text;
			try
			{
				text = File.ReadAllText(file);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				stderr.WriteLine($"ошибка: не удалось прочитать {file}: {exception.Message}");
				failed = true;
				continue;
			}

			string formatted = SourceFormatter.Format(text);
			if (string.Equals(formatted, text, StringComparison.Ordinal))
				continue;

			changed++;
			if (check)
			{
				stderr.WriteLine($"требует форматирования: {file}");
				continue;
			}

			if (toStdout)
			{
				stdout.Write(formatted);
				continue;
			}

			try
			{
				File.WriteAllText(file, formatted, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				stderr.WriteLine($"ошибка: не удалось записать {file}: {exception.Message}");
				failed = true;
			}
		}

		if (check)
			return changed > 0 ? 1 : 0;
		if (!toStdout)
			stdout.WriteLine($"отформатировано файлов: {changed}");
		return failed ? 1 : 0;
	}

	private static List<string> Collect(IEnumerable<string> paths, TextWriter stderr, out bool failed)
	{
		failed = false;
		var files = new List<string>();
		foreach (string path in paths)
		{
			if (File.Exists(path))
			{
				if (!path.EndsWith(".kern", StringComparison.OrdinalIgnoreCase))
				{
					stderr.WriteLine($"ошибка: kernc fmt принимает файлы .kern: {path}");
					failed = true;
					continue;
				}

				files.Add(path);
				continue;
			}

			if (!Directory.Exists(path))
			{
				stderr.WriteLine($"ошибка: путь не найден: {path}");
				failed = true;
				continue;
			}

			foreach (string file in Directory.EnumerateFiles(path, "*.kern", SearchOption.AllDirectories))
			{
				if (IsBuildDirectory(file))
					continue;
				files.Add(file);
			}
		}

		files.Sort(StringComparer.Ordinal);
		return files;
	}

	private static bool IsBuildDirectory(string file)
	{
		string separator = Path.DirectorySeparatorChar.ToString();
		foreach (string skipped in new[] { "bin", "obj", "out", "dist", ".git" })
			if (file.Contains($"{separator}{skipped}{separator}", StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}
}
