using System.Reflection;
using System.Text.RegularExpressions;
using Kern.Binding.Catalog;
using Kern.Compiler.Compilation;
using Kern.Lsp.Protocol;
using Kern.Runtime.Build;

namespace Kern.Lsp.Analysis;

/// <summary>
/// Компилирует открытые документы (плюс `.kern`-файлы проекта с диска) как один проект и
/// отдаёт диагностику. Каталог нативов — <c>Kern.Runtime</c> плюс найденные
/// <c>libs/**/Kern.Lib.*.dll</c>; это позволяет видеть импорты и подсказки внешних библиотек.
/// </summary>
public sealed class AnalysisService
{
	private NativeCatalog _catalog;
	private string? _root;

	public AnalysisService()
		: this(NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly))
	{
	}

	public AnalysisService(NativeCatalog catalog) => _catalog = catalog;

	public ProjectCompileResult? Last { get; private set; }

	/// <summary>Семантический снимок последнего успешного/частичного анализа.</summary>
	public LspIndex? Index { get; private set; }

	/// <summary>Задать корень workspace и пересобрать каталог с внешними библиотеками.</summary>
	public void SetRoot(string? root)
	{
		_root = root;
		_catalog = BuildCatalog(root);
	}

	public IReadOnlyList<ProjectDiagnostic> Analyze(DocumentStore documents)
	{
		ArgumentNullException.ThrowIfNull(documents);
		var files = new List<ProjectSourceFile>(documents.ToProject());
		AddProjectFiles(files, documents);
		ConfigureBuildScripts(files);

		if (files.Count == 0)
		{
			Last = null;
			Index = null;
			return [];
		}

		try
		{
			Last = ProjectCompiler.Compile(files, "lsp", _catalog);
			Index = Last.Module is { } module
				? new LspIndex(module.Symbol, ModuleNames(files, _catalog))
				: null;
			return Last.Diagnostics;
		}
		catch (Exception exception)
		{
			// LSP не должен падать из-за одного битого документа: отдаём ошибку как строку.
			Last = null;
			Index = null;
			return
			[
				new ProjectDiagnostic(
					files[0].RelativePath,
					new Kern.Core.Diagnostics.Diagnostic("KRN0000", "ошибка анализа: " + exception.Message, default))
			];
		}
	}

	/// <summary>Добавить неоткрытые `.kern`-файлы проекта, чтобы `use` между файлами работал.</summary>
	private void AddProjectFiles(List<ProjectSourceFile> files, DocumentStore documents)
	{
		if (string.IsNullOrEmpty(_root) || !Directory.Exists(_root))
			return;

		string sourceRoot = Path.Combine(_root, "src");
		IEnumerable<string> paths = Directory.Exists(sourceRoot)
			? Directory.EnumerateFiles(sourceRoot, "*.kern", SearchOption.AllDirectories)
			: Directory.EnumerateFiles(_root, "*.kern", SearchOption.TopDirectoryOnly);
		var known = new HashSet<string>(
			files.Select(file => file.RelativePath.Replace('\\', '/')),
			StringComparer.OrdinalIgnoreCase);

		foreach (string path in paths.OrderBy(path => path, StringComparer.Ordinal))
		{
			string relative = Path.GetRelativePath(_root, path).Replace('\\', '/');
			if (!known.Add(relative))
				continue;
			try
			{
				files.Add(new ProjectSourceFile(relative, File.ReadAllText(path)));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}

		files.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
	}

	/// <summary>
	/// <c>build.kern</c> компилируется CLI вместе с встроенным <c>build_prelude</c>; LSP
	/// подставляет тот же prelude (с заглушкой <c>__build_env</c>), чтобы <c>workspace</c>,
	/// <c>target</c>, <c>profile</c> и их члены были видны в редакторе.
	/// </summary>
	private static void ConfigureBuildScripts(List<ProjectSourceFile> files)
	{
		var builds = files
			.Select((file, index) => (File: file, Index: index))
			.Where(item => string.Equals(Path.GetFileName(item.File.RelativePath), "build.kern",
				StringComparison.OrdinalIgnoreCase))
			.ToList();
		if (builds.Count == 0 ||
			files.Any(file => string.Equals(file.RelativePath, BuildPrelude.Path, StringComparison.Ordinal)))
			return;

		foreach (var (file, index) in builds)
		{
			string text = Regex.Replace(
				file.Text,
				@"(?m)^\s*use\s+kern\.build[^\r\n]*(?:\r?\n)?",
				string.Empty);
			files[index] = new ProjectSourceFile(file.RelativePath, text + BuildPrelude.Import);
		}
		files.Add(new ProjectSourceFile(BuildPrelude.Path, BuildPrelude.AnalysisSource));
	}

	private static IReadOnlyList<string> ModuleNames(		IReadOnlyList<ProjectSourceFile> files,
		NativeCatalog catalog)
	{
		var names = new HashSet<string>(catalog.Modules.Select(module => module.Name), StringComparer.Ordinal);
		foreach (ProjectSourceFile file in files)
		{
			string module = Path.ChangeExtension(file.RelativePath, null)?.Replace('\\', '/').Replace('/', '.')
				?? file.RelativePath;
			if (module.StartsWith("src.", StringComparison.Ordinal))
				module = module[4..];
			names.Add(module);
		}
		return [.. names.OrderBy(name => name, StringComparer.Ordinal)];
	}

	private static NativeCatalog BuildCatalog(string? root)
	{
		var assemblies = new List<Assembly> { typeof(Kern.Runtime.Core.CoreModule).Assembly };
		if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
		{
			foreach (string library in LibraryFiles(root))
			{
				try
				{
					assemblies.Add(Assembly.LoadFrom(library));
				}
				catch (Exception exception) when (exception is IOException or BadImageFormatException or FileLoadException)
				{
				}
			}
		}
		try
		{
			return NativeCatalog.Scan(assemblies);
		}
		catch (Exception)
		{
			return NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);
		}
	}

	private static IEnumerable<string> LibraryFiles(string root)
	{
		var roots = new List<string> { Path.Combine(root, "libs"), Path.Combine(root, "libs", "_out") };
		foreach (string candidate in roots)
		{
			if (!Directory.Exists(candidate))
				continue;
			IEnumerable<string> files;
			try
			{
				files = Directory.EnumerateFiles(candidate, "Kern.Lib.*.dll", SearchOption.AllDirectories);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				continue;
			}
			foreach (string file in files)
				yield return file;
		}
	}
}
