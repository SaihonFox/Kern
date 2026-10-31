using System.Reflection;
using Kern.Binding.Catalog;
using Kern.Cli.Kbc;
using Kern.Cli.Macros;
using Kern.Binding.Marshalling;
using Kern.Bytecode;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Bound.Declarations;
using Kern.Compiler.Semantics.Project;
using Kern.Core;
using Kern.Runtime.Core;
using Kern.Vm.Execution;

namespace Kern.Cli.Infrastructure;

/// <summary>Сквозной конвейер: проект → семантика → байткод → VM.</summary>
internal static class Pipeline
{
	private static readonly Dictionary<string, NativeCatalog> Catalogs = [];

	/// <summary>Каталог нативов: ядро (Kern.Runtime) + внешние библиотеки из libs (см. LibraryLoader). Кэшируется по набору библиотек.</summary>
	private static NativeCatalog GetCatalog(string? projectDir)
	{
		var libs = LibraryLoader.Load(LibraryLoader.StandardRoots(projectDir));
		string key = string.Join('|', libs.Select(a => a.GetName().Name).Order());
		lock (Catalogs)
		{
			if (!Catalogs.TryGetValue(key, out var catalog))
				Catalogs[key] = catalog = new NativeCatalog([typeof(CoreApi).Assembly, .. libs]);
			return catalog;
		}
	}

	public static ProjectLoader CreateLoader(string? projectDir = null)
	{
		var natives = new CatalogNativeSource(GetCatalog(projectDir));
		var std = new EmbeddedStdSource(Assembly.Load("Kern.Std"));
		ISourceTransform transform = new MacroEngine(natives, std, projectDir);
		try
		{
			var libs = KbcConfig.Load(projectDir);
			if (libs.Count > 0) transform = new KbcStubTransform(transform, libs);
		}
		catch (KbcException e)
		{
			Console.Error.WriteLine($"error: {e.Message}");
		}
		return new ProjectLoader(natives, std, transform);
	}

	/// <summary>Загружает путь (папка с kern.toml / одиночный .kern) и печатает диагностики.</summary>
	public static ProjectModel? Load(string path, out string name)
	{
		var loader = CreateLoader(Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path)));
		if (Directory.Exists(path))
		{
			name = new DirectoryInfo(Path.GetFullPath(path)).Name;
			return loader.LoadDirectory(path);
		}
		if (File.Exists(path))
		{
			name = Path.GetFileNameWithoutExtension(path);
			return loader.LoadText(name, path, File.ReadAllText(path));
		}
		Console.Error.WriteLine($"error: путь не найден: {path}");
		name = "";
		return null;
	}

	/// <summary>Печать диагностик с фрагментом строки; true — есть ошибки.</summary>
	public static bool Report(IEnumerable<Diagnostic> diagnostics, bool warnings = true)
	{
		bool errors = false;
		foreach (var d in diagnostics)
		{
			if (d.Severity == Severity.Info || (d.Severity == Severity.Warning && !warnings)) continue;
			errors |= d.Severity == Severity.Error;
			Console.Error.WriteLine(d);
			if (d.File is { } f)
			{
				var (line, col) = f.GetLineColumn(d.Span.Start);
				string text = f.GetLineText(line);
				// отступ под кареткой копирует табы строки, чтобы `^` стояла ровно под ошибкой
				var pad = new string([.. text.Take(Math.Max(0, col - 1)).Select(ch => ch == '\t' ? '\t' : ' ')]);
				Console.Error.WriteLine("    " + text);
				Console.Error.WriteLine("    " + pad + new string('^', Math.Max(1, Math.Min(d.Span.Length, 40))));
			}
		}
		return errors;
	}

	/// <summary>Проверка без генерации кода.</summary>
	public static BoundProgram? Analyze(string path, out string name)
	{
		var project = Load(path, out name);
		return project is null ? null : Analyze(project);
	}

	public static BoundProgram? Analyze(ProjectModel project)
	{
		bool bad = Report(project.Diagnostics);
		if (bad) return null;
		int printed = project.Diagnostics.Count;
		var program = SemanticAnalyzer.Analyze(project);
		bad = Report(project.Diagnostics.Skip(printed));
		return bad ? null : program;
	}

	public static KbcModule? Compile(string path, int optimization)
	{
		var program = Analyze(path, out var name);
		if (program is null) return null;
		var module = CodeGenerator.Generate(program, name, optimization);
		MakePathsRelative(module, ProjectRoot(path));
		return Link(module, ProjectRoot(path));
	}

	/// <summary>Подключить .kbc из kern.toml (`kbc = [...]`); при ошибке печатает её и возвращает null.</summary>
	internal static KbcModule? Link(KbcModule module, string projectDir)
	{
		try
		{
			KbcLinker.LinkProject(module, projectDir);
			return module;
		}
		catch (KbcException e)
		{
			Console.Error.WriteLine($"error: {e.Message}");
			return null;
		}
	}

	/// <summary>Папка проекта: сама папка либо папка одиночного .kern-файла.</summary>
	public static string ProjectRoot(string path) =>
		Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path))!;

	/// <summary>
	/// В .kbc имена исходников хранятся для трассировки паники. Абсолютный путь привязывал бы файл к машине сборки,
	/// поэтому пути записываются относительно папки проекта (`src/main.kern`).
	/// </summary>
	public static void MakePathsRelative(KbcModule module, string root)
	{
		foreach (var f in module.Functions)
			if (f.SourceFile is { } p && Path.IsPathRooted(p))
				f.SourceFile = Path.GetRelativePath(root, p).Replace('\\', '/');
	}

	/// <summary>Цель сборки: несколько каталогов исходников (свои + зависимостей) как один проект.</summary>
	public static KbcModule? CompileSources(string name, string root, IReadOnlyList<string> srcDirs, string? entry, int optimization)
	{
		var program = Analyze(CreateLoader(root).LoadSources(name, root, srcDirs, entry));
		if (program is null) return null;
		var module = CodeGenerator.Generate(program, name, optimization);
		MakePathsRelative(module, root);
		return Link(module, root);
	}

	/// <summary>Перед исполнением внешние библиотеки должны быть в процессе: NativeResolver ищет члены по ключу среди загруженных сборок.</summary>
	public static Interpreter CreateVm(KbcModule module, string? projectDir = null)
	{
		// NativeResolver ищет члены по ключу среди ЗАГРУЖЕННЫХ сборок: при запуске готового .kbc Kern.Runtime ещё не трогали,
		// поэтому её нужно загрузить явно (иначе «нативный член не найден: Kern.Runtime…»).
		_ = typeof(CoreApi).Assembly;
		LibraryLoader.Load(LibraryLoader.StandardRoots(projectDir));
		return new(module, new NativeResolver());
	}
}
