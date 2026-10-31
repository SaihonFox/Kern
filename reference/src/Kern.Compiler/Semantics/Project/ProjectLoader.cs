using System.Text;
using Kern.Compiler.Parsing;
using Kern.Compiler.Semantics.Declare;
using Kern.Core;

namespace Kern.Compiler.Semantics.Project;

/// <summary>Загрузка проекта: поиск исходников, парсинг (UTF-8), сбор объявлений. Имя модуля — из `project a.b:` либо из пути файла.</summary>
public sealed class ProjectLoader(INativeSymbolSource natives, IStdSource std, ISourceTransform? transform = null)
{
	public ProjectModel LoadDirectory(string root)
	{
		var diags = new DiagnosticBag();
		var toml = Path.Combine(root, "kern.toml");
		string name = new DirectoryInfo(root).Name;
		string srcRel = "src";
		string? entry = null;
		if (File.Exists(toml))
		{
			foreach (var raw in File.ReadAllLines(toml, new UTF8Encoding(false)))
			{
				var line = raw.Trim();
				int eq = line.IndexOf('=');
				if (eq < 0 || line.StartsWith('#') || line.StartsWith('[')) continue;
				var key = line[..eq].Trim();
				var val = line[(eq + 1)..].Trim().Trim('"');
				switch (key)
				{
					case "name": name = val; break;
					case "src" or "source": srcRel = val; break;
					case "entry": entry = val; break;
				}
			}
		}
		var srcDir = Path.Combine(root, srcRel);
		if (!Directory.Exists(srcDir)) srcDir = root;

		var files = Directory.EnumerateFiles(srcDir, "*.kern", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
			.Select(f => (Path: f, Text: File.ReadAllText(f, new UTF8Encoding(false)), Module: ModuleFromPath(srcDir, f)));
		return Load(name, files, diags, root, srcDir, entry);
	}

	/// <summary>Несколько каталогов исходников как один проект (цель сборки + исходники её зависимостей).</summary>
	public ProjectModel LoadSources(string name, string root, IReadOnlyList<string> srcDirs, string? entry)
	{
		var diags = new DiagnosticBag();
		var files = srcDirs.SelectMany(dir => Directory.EnumerateFiles(dir, "*.kern", SearchOption.AllDirectories)
			.Order(StringComparer.Ordinal)
			.Select(f => (Path: f, Text: File.ReadAllText(f, new UTF8Encoding(false)), Module: ModuleFromPath(dir, f))));
		return Load(name, files, diags, root, srcDirs.Count > 0 ? srcDirs[0] : root, entry);
	}

	public ProjectModel LoadText(string name, string path, string text)
	{
		var diags = new DiagnosticBag();
		return Load(name, [(path, text, Path.GetFileNameWithoutExtension(path))], diags, null, null, null);
	}

	/// <summary>Проект из готового набора файлов (без kern.toml): так компилируется «проект макросов».</summary>
	public ProjectModel LoadFiles(string name, IReadOnlyList<SourceText> files) =>
		Load(name, files.Select(f => (f.Path, f.Text, f.Module)), new DiagnosticBag(), null, null, null);

	private ProjectModel Load(string name, IEnumerable<(string Path, string Text, string Module)> files, DiagnosticBag diags,
		string? root, string? srcDir, string? entry)
	{
		if (transform is not null)
			files = transform.Transform(name, [.. files.Select(f => new SourceText(f.Path, f.Text, f.Module))], diags)
				.Select(f => (f.Path, f.Text, f.Module)).ToList();
		var units = new List<SourceUnit>();
		var userUnits = new List<SourceUnit>();
		foreach (var (path, text, module) in files)
		{
			var u = ParserFacade.ParseText(path, text, name, diags);
			userUnits.Add(new SourceUnit(u, DeclaredName(u) ?? module, false));
		}

		// Ленивые импорты: std-модуль попадает в компиляцию (а значит и в байткод/память) только если он
		// входит в prelude либо достижим по `use` из пользовательского кода (транзитивно).
		var stdAll = new List<(SourceUnit Unit, DiagnosticBag Diags)>();
		foreach (var (path, text, module) in std.Files().Select(s => (s.Path, s.Text, Module: StdLibrary.ModuleNameOf(s.Path))))
		{
			var bag = new DiagnosticBag();
			var u = ParserFacade.ParseText(path, text, name, bag);
			stdAll.Add((new SourceUnit(u, DeclaredName(u) ?? module, true), bag));
		}
		var needed = new HashSet<string>(DeclarationCollector.PreludeModules);
		var pending = new Queue<IEnumerable<Syntax.UseDecl>>();
		pending.Enqueue(userUnits.SelectMany(u => u.Unit.Uses));
		var included = new HashSet<string>();
		while (true)
		{
			while (pending.Count > 0)
				foreach (var use in pending.Dequeue())
				{
					string full = string.Join('.', use.Path);
					needed.Add(full);
					foreach (var item in use.Items) needed.Add(full + "." + item);
				}
			var fresh = stdAll.Where(x => !included.Contains(x.Unit.ModuleName) && IsNeeded(x.Unit.ModuleName, needed)).ToList();
			if (fresh.Count == 0) break;
			foreach (var (unit, bag) in fresh)
			{
				included.Add(unit.ModuleName);
				units.Add(unit);
				diags.AddRange(bag);
				pending.Enqueue(unit.Unit.Uses);
			}
		}
		units.AddRange(userUnits);
		return new DeclarationCollector(natives, diags).Collect(name, units, root, srcDir, entry);
	}

	private static bool IsNeeded(string module, HashSet<string> needed) =>
		needed.Contains(module) || needed.Any(n => n.StartsWith(module + ".", StringComparison.Ordinal));

	private static string? DeclaredName(Syntax.CompilationUnit u) => u.ProjectName is { Length: > 0 } n ? n : null;

	/// <summary>`src/a/b/c.kern` → `a.b.c`; `src/main.kern` → `main`.</summary>
	public static string ModuleFromPath(string srcDir, string file)
	{
		var rel = Path.GetRelativePath(srcDir, file).Replace('\\', '/');
		return rel[..^5].Replace('/', '.');
	}
}
