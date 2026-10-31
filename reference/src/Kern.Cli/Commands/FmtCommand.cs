using Kern.Compiler.Formatting;

namespace Kern.Cli.Commands;

/// <summary>`kernc fmt`: нормализация отступов и пробелов в файлах .kern (на месте, либо проверка/вывод в stdout).</summary>
internal static class FmtCommand
{
	private static readonly HashSet<string> SkipDirs = ["bin", "obj", "build", "dist", ".git", ".kern"];

	public static int Run(IReadOnlyList<string> args)
	{
		bool check = args.Contains("--check");
		bool stdout = args.Contains("--stdout");
		var paths = args.Where(a => !a.StartsWith("--")).DefaultIfEmpty(".").ToList();

		var files = paths.SelectMany(Expand).Distinct().ToList();
		if (files.Count == 0)
		{
			Console.Error.WriteLine("error: файлы .kern не найдены");
			return 2;
		}

		int changed = 0, failed = 0;
		foreach (var file in files)
		{
			string text = File.ReadAllText(file);
			var r = SourceFormatter.Format(file, text);
			if (r.Text is null)
			{
				Console.Error.WriteLine($"{file}: {r.Error}");
				failed++;
				continue;
			}
			if (stdout) { Console.Out.Write(r.Text); continue; }
			if (r.Text == text) continue;
			changed++;
			if (check) Console.Out.WriteLine($"нужно форматирование: {file}");
			else
			{
				File.WriteAllText(file, r.Text);
				Console.Out.WriteLine($"отформатирован: {file}");
			}
		}

		if (!stdout) Console.Out.WriteLine($"файлов: {files.Count}, {(check ? "требуют форматирования" : "изменено")}: {changed}, с ошибками: {failed}");
		return failed > 0 || (check && changed > 0) ? 1 : 0;
	}

	private static IEnumerable<string> Expand(string path)
	{
		if (File.Exists(path)) return [path];
		if (!Directory.Exists(path)) return [];
		return Walk(path);
	}

	private static IEnumerable<string> Walk(string dir)
	{
		foreach (var f in Directory.EnumerateFiles(dir, "*.kern")) yield return f;
		foreach (var d in Directory.EnumerateDirectories(dir))
			if (!SkipDirs.Contains(Path.GetFileName(d)))
				foreach (var f in Walk(d)) yield return f;
	}
}
