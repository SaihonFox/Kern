using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Bytecode;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Compilation;
using Kern.Compiler.Semantics.Bound;
using Kern.Core.Diagnostics;
using Kern.Runtime.Core;
using Kern.Vm.Execution;

namespace Kern.Lib.Transpile.Core;

/// <summary>
/// Загрузка и семантический анализ Kern-кода для транспилятора: тот же конвейер, что у
/// <c>kernc</c> (parser → символы → объявления → binder), но без генерации байткода.
/// Скрипт подстановок дополнительно компилируется в байткод и исполняется в VM.
/// </summary>
public static class ProgramLoader
{
	/// <summary>Каталог нативов ядра: без него не разрешаются <c>println</c>, <c>list.len</c> и т. п.</summary>
	public static NativeCatalog CreateCatalog() => NativeCatalog.Scan(typeof(CoreModule).Assembly);

	/// <summary>Один файл или папка проекта; папка склеивается в один проект (модуль на файл).</summary>
	public static BoundModule LoadPath(string path)
	{
		ArgumentNullException.ThrowIfNull(path);
		string full = Path.GetFullPath(path);
		if (Directory.Exists(full))
			return LoadFiles(ReadDirectory(full), new DirectoryInfo(full).Name);

		if (!File.Exists(full))
			throw new FileNotFoundException($"путь не найден: {full}");

		string name = Path.GetFileNameWithoutExtension(full);
		return LoadFiles([new ProjectSourceFile(Path.GetFileName(full), File.ReadAllText(full))], name);
	}

	public static BoundModule LoadText(string name, string source)
	{
		ArgumentNullException.ThrowIfNull(source);
		return LoadFiles([new ProjectSourceFile(name + ".kern", source)], name);
	}

	private static BoundModule LoadFiles(IReadOnlyList<ProjectSourceFile> files, string name)
	{
		NativeCatalog catalog = CreateCatalog();
		ProjectCompileResult result = ProjectCompiler.Compile(files, name, catalog);
		if (!result.Success || result.Module is null)
			throw new InvalidOperationException(Describe(result));
		return result.Module;
	}

	/// <summary>Скомпилировать Kern-скрипт в байткод и поднять VM (для скрипта подстановок).</summary>
	public static Interpreter LoadScript(string path)
	{
		ArgumentNullException.ThrowIfNull(path);
		string full = Path.GetFullPath(path);
		if (!File.Exists(full))
			throw new FileNotFoundException($"скрипт не найден: {full}");

		NativeCatalog catalog = CreateCatalog();
		string name = Path.GetFileNameWithoutExtension(full);
		var files = new[] { new ProjectSourceFile(Path.GetFileName(full), File.ReadAllText(full)) };
		ProjectCompileResult result = ProjectCompiler.Compile(files, name, catalog);
		if (!result.Success || result.Module is null)
			throw new InvalidOperationException(Describe(result));

		CodegenResult generated = new CodeGenerator().Generate(result.Module, result.Bundle);
		IReadOnlyList<Diagnostic> errors = [.. generated.Diagnostics.Where(diagnostic => diagnostic.IsError)];
		if (errors.Count > 0)
			throw new InvalidOperationException(
				"ошибки генерации скрипта подстановок:\n" +
				string.Join('\n', errors.Select(diagnostic => DiagnosticText.Format(diagnostic, result.Bundle))));

		return new Interpreter(generated.Module, new NativeResolver(catalog));
	}

	private static List<ProjectSourceFile> ReadDirectory(string root)
	{
		string[] paths = [.. Directory.EnumerateFiles(root, "*.kern", SearchOption.AllDirectories)];
		Array.Sort(paths, StringComparer.Ordinal);
		var files = new List<ProjectSourceFile>(paths.Length);
		foreach (string path in paths)
		{
			string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
			files.Add(new ProjectSourceFile(relative, File.ReadAllText(path)));
		}

		if (files.Count == 0)
			throw new InvalidOperationException($"в папке {root} нет файлов .kern");
		return files;
	}

	private static string Describe(ProjectCompileResult result)
	{
		var lines = new List<string>();
		foreach (ProjectDiagnostic diagnostic in result.Diagnostics)
			lines.Add(DiagnosticText.Format(diagnostic.Diagnostic, result.Bundle));
		lines.AddRange(result.NativeProblems.Select(problem => $"натив: {problem}"));
		return lines.Count == 0
			? "не удалось скомпилировать Kern-код"
			: "ошибки Kern-кода:\n" + string.Join('\n', lines);
	}
}
