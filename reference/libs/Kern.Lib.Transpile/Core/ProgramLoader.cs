using System.Reflection;
using System.Text;
using Kern.Binding.Catalog;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Bound.Declarations;
using Kern.Compiler.Semantics.Project;
using Kern.Core;
using Kern.Runtime.Core;

namespace Kern.Lib.Transpile.Core;

/// <summary>Загрузка и семантический анализ Kern-кода для транспилятора (тот же конвейер, что у kernc, без генерации байткода).</summary>
public static class ProgramLoader
{
	private static ProjectLoader CreateLoader(string? dir)
	{
		var libs = LibraryLoader.Load(LibraryLoader.StandardRoots(dir));
		var catalog = new NativeCatalog([typeof(CoreApi).Assembly, .. libs]);
		return new ProjectLoader(new CatalogNativeSource(catalog), new EmbeddedStdSource(Assembly.Load("Kern.Std")));
	}

	/// <summary>Папка проекта (kern.toml / src) или одиночный .kern-файл.</summary>
	public static BoundProgram LoadPath(string path)
	{
		path = Path.GetFullPath(path);
		if (Directory.Exists(path))
			return Analyze(CreateLoader(path).LoadDirectory(path));
		if (!File.Exists(path)) throw new FileNotFoundException($"путь не найден: {path}");
		return Analyze(CreateLoader(Path.GetDirectoryName(path)).LoadText(Path.GetFileNameWithoutExtension(path), path, File.ReadAllText(path, new UTF8Encoding(false))));
	}

	public static BoundProgram LoadText(string name, string text) => Analyze(CreateLoader(null).LoadText(name, name + ".kern", text));

	private static BoundProgram Analyze(ProjectModel project)
	{
		Check(project.Diagnostics, 0);
		int printed = project.Diagnostics.Count;
		var program = SemanticAnalyzer.Analyze(project);
		Check(project.Diagnostics, printed);
		return program;
	}

	private static void Check(DiagnosticBag bag, int skip)
	{
		var errors = bag.Skip(skip).Where(d => d.Severity == Severity.Error).Select(d => d.ToString()).ToList();
		if (errors.Count > 0) throw new InvalidOperationException("ошибки Kern-кода:\n" + string.Join('\n', errors));
	}
}
