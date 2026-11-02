using System.Text;
using Kern.Binding.Catalog;
using Kern.Cli.Build;
using Kern.Cli.Infrastructure;
using Kern.Compiler.Semantics;
using Kern.Runtime.Core;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc doc &lt;файл|папка&gt; [-o &lt;папка&gt;]</c>: собирает Markdown из <c>///</c>
/// и выполняет doctest-блоки <c>```kern</c>. Ожидаемый вывод задаётся строками
/// <c>// expect:</c>; без маркера вывод не проверяется.
/// </summary>
internal static class DocCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		string? source = null;
		string? output = null;
		for (var index = 0; index < args.Length; index++)
		{
			string argument = args[index];
			if (argument == "-o")
			{
				if (index + 1 >= args.Length)
				{
					stderr.WriteLine("ошибка: параметр -o требует папку");
					return 2;
				}

				output = args[++index];
				continue;
			}

			if (argument.StartsWith('-') || source is not null)
			{
				stderr.WriteLine("ошибка: использование: kernc doc <файл.kern|папка> [-o <папка>]");
				return 2;
			}

			source = argument;
		}

		if (source is null)
		{
			stderr.WriteLine("ошибка: укажите файл .kern или папку проекта");
			return 2;
		}

		string full = Path.GetFullPath(source);
		bool directory = Directory.Exists(full);
		if (!directory && !File.Exists(full))
		{
			stderr.WriteLine($"ошибка: путь не найден: {full}");
			return 1;
		}

		string root = directory ? full : Path.GetDirectoryName(full) ?? ".";
		string destination = output is null ? Path.Combine(root, "doc") : Path.GetFullPath(output);
		IReadOnlyList<string> files = directory ? ProjectFiles(full) : [full];
		if (files.Count == 0)
		{
			stderr.WriteLine($"ошибка: в {full} нет .kern-файлов");
			return 1;
		}

		NativeCatalog catalog = NativeCatalog.Scan(typeof(CoreModule).Assembly);
		var failures = 0;
		var generated = 0;
		foreach (string file in files)
		{
			IReadOnlyList<DocEntry> entries = ReadEntries(file);
			if (entries.Count == 0)
				continue;
			foreach (DocEntry entry in entries)
			{
				foreach (Doctest test in entry.Tests)
				{
					if (!RunDoctest(file, entry.Signature, test, catalog, stderr))
						failures++;
				}
			}

			string relative = Path.GetRelativePath(root, file);
			string target = Path.Combine(destination, Path.ChangeExtension(relative, ".md"));
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.WriteAllText(target, Markdown(file, entries), new UTF8Encoding(false));
			generated++;
			stdout.WriteLine(target);
		}

		if (failures > 0)
		{
			stderr.WriteLine($"doctest: провалено {failures}");
			return 1;
		}

		stdout.WriteLine($"doc: {generated} файл(ов)");
		return 0;
	}

	/// <summary>Документация и doctest-блоки одного исходника (для тестов).</summary>
	internal static IReadOnlyList<DocEntry> ReadEntries(string path) =>
		Parse(File.ReadAllLines(path));

	internal static IReadOnlyList<DocEntry> Parse(IReadOnlyList<string> lines)
	{
		var entries = new List<DocEntry>();
		for (var index = 0; index < lines.Count; index++)
		{
			string trimmed = lines[index].TrimStart();
			if (!trimmed.StartsWith("///", StringComparison.Ordinal))
				continue;

			var docs = new List<string>();
			while (index < lines.Count && lines[index].TrimStart().StartsWith("///", StringComparison.Ordinal))
			{
				string text = lines[index].TrimStart()[3..];
				docs.Add(text.StartsWith(' ') ? text[1..] : text);
				index++;
			}

			while (index < lines.Count && string.IsNullOrWhiteSpace(lines[index]))
				index++;
			while (index < lines.Count && lines[index].TrimStart().StartsWith("#[", StringComparison.Ordinal))
				index++;
			if (index >= lines.Count)
				break;
			string signature = lines[index].Trim();
			if (signature.Length == 0)
				continue;
			string markdown = string.Join("\n", docs);
			entries.Add(new DocEntry(signature, markdown, ExtractTests(markdown)));
		}

		return entries;
	}

	private static IReadOnlyList<Doctest> ExtractTests(string markdown)
	{
		var tests = new List<Doctest>();
		string[] lines = markdown.Replace("\r\n", "\n").Split('\n');
		for (var index = 0; index < lines.Length; index++)
		{
			if (!lines[index].TrimStart().StartsWith("```kern", StringComparison.OrdinalIgnoreCase))
				continue;
			var code = new List<string>();
			index++;
			while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
			{
				code.Add(lines[index]);
				index++;
			}

			if (code.Count == 0)
				continue;
			var expected = code
				.Where(line => line.TrimStart().StartsWith("// expect:", StringComparison.Ordinal))
				.Select(line => line[(line.IndexOf("// expect:", StringComparison.Ordinal) + "// expect:".Length)..].Trim())
				.ToList();
			tests.Add(new Doctest(string.Join("\n", code), expected.Count == 0 ? null : string.Join("\n", expected)));
		}

		return tests;
	}

	private static bool RunDoctest(
		string sourcePath,
		string signature,
		Doctest test,
		NativeCatalog catalog,
		TextWriter stderr)
	{
		CompileResult compiled;
		try
		{
			compiled = SourceCompiler.CompileFiles(
				[("doctest.kern", test.Code)],
				"doctest",
				catalog,
				CompileOptions.Default);
		}
		catch (Exception exception)
		{
			stderr.WriteLine($"{sourcePath}: {signature}: doctest: {exception.Message}");
			return false;
		}

		SourceCompiler.Report(compiled, stderr);
		if (!compiled.Success)
		{
			stderr.WriteLine($"{sourcePath}: {signature}: doctest не компилируется");
			return false;
		}

		if (!System.Text.RegularExpressions.Regex.IsMatch(test.Code, @"\bfun\s+main\b"))
			return true;

		TextWriter original = Console.Out;
		var writer = new StringWriter { NewLine = "\n" };
		Console.SetOut(writer);
		int code;
		try
		{
			code = RunCommand.RunCompiled(
				compiled.Program!,
				[],
				null,
				stderr,
				workingDirectory: Path.GetDirectoryName(Path.GetFullPath(sourcePath)));
		}
		finally
		{
			Console.SetOut(original);
		}

		if (code != 0)
		{
			stderr.WriteLine($"{sourcePath}: {signature}: doctest завершился с кодом {code}");
			return false;
		}

		if (test.Expected is { } expected)
		{
			string actual = writer.ToString().Replace("\r\n", "\n").TrimEnd();
			if (!string.Equals(actual, expected.TrimEnd(), StringComparison.Ordinal))
			{
				stderr.WriteLine($"{sourcePath}: {signature}: doctest: ожидалось '{expected}', получено '{actual}'");
				return false;
			}
		}

		return true;
	}

	private static IReadOnlyList<string> ProjectFiles(string root)
	{
		string source = Path.Combine(root, "src");
		string directory = Directory.Exists(source) ? source : root;
		return
		[
			.. Directory
				.EnumerateFiles(directory, "*.kern", SearchOption.AllDirectories)
				.OrderBy(path => path, StringComparer.Ordinal)
		];
	}

	private static string Markdown(string path, IReadOnlyList<DocEntry> entries)
	{
		var builder = new StringBuilder();
		builder.Append("# ").AppendLine(Path.GetFileName(path));
		foreach (DocEntry entry in entries)
		{
			builder.AppendLine();
			builder.Append("## `").Append(entry.Signature).AppendLine("`");
			builder.AppendLine();
			builder.AppendLine(entry.Markdown);
		}

		return builder.ToString();
	}
}

internal sealed record DocEntry(string Signature, string Markdown, IReadOnlyList<Doctest> Tests);

internal sealed record Doctest(string Code, string? Expected);
