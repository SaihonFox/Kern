using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Bytecode;
using Kern.Cli.Infrastructure;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Cli.Build;

/// <summary>
/// Загруженный проект: <c>build.kern</c>, выполненный один раз, и живой интерпретатор, в котором
/// продолжают жить замыкания задач (docs/BUILDER.md §1, §3). Папка без <c>build.kern</c> даёт
/// модель по соглашению с одной синтетической целью (§2); интерпретатора у неё нет.
/// </summary>
internal sealed class BuildSession
{
	/// <summary>Имя сценария сборки в корне проекта.</summary>
	public const string ScriptName = "build.kern";
	private const string ScriptModule = "build";

	private BuildSession(string root, string? scriptPath, BuildModel workspace, Interpreter? interpreter)
	{
		Root = root;
		ScriptPath = scriptPath;
		Workspace = workspace;
		Interpreter = interpreter;
	}

	/// <summary>Абсолютный путь корня проекта без завершающего разделителя.</summary>
	public string Root { get; }

	/// <summary>Путь к настоящей <c>build.kern</c>; <c>null</c> в режиме соглашения.</summary>
	public string? ScriptPath { get; }

	public BuildModel Workspace { get; }

	/// <summary>Интерпретатор сценария сборки; <c>null</c> у проекта по соглашению.</summary>
	public Interpreter? Interpreter { get; }

	/// <summary>Уровень оптимизации из CLI (<c>-O0..-O2</c>); <c>null</c> — из build.kern/по умолчанию.</summary>
	public int? OptimizeOverride { get; set; }

	public bool HasBuildFile => ScriptPath is not null;

	/// <summary>
	/// Читает проект и выполняет <c>build.kern</c>. Ошибки чтения, компиляции и выполнения
	/// приходят как <see cref="BuildException"/> с готовым русским текстом.
	/// </summary>
	public static BuildSession Open(string directory)
	{
		ArgumentNullException.ThrowIfNull(directory);
		string root = ResolveRoot(directory);
		string scriptPath = Path.Combine(root, ScriptName);
		if (!File.Exists(scriptPath))
			return OpenConvention(root);

		string text = ReadScript(scriptPath);
		// Импорт добавляется в конец файла: строки пользовательского сценария не сдвигаются,
		// поэтому диагностики указывают на настоящие строки build.kern.
		(string Path, string Text)[] files =
		[
			(ScriptName, text + BuildPrelude.Import),
			(BuildPrelude.Path, BuildPrelude.Source),
		];

		NativeCatalog catalog = NativeCatalog.Scan(
			typeof(CoreModule).Assembly,
			typeof(BuildNativeModule).Assembly);
		CompileResult compiled = SourceCompiler.CompileFiles(files, ScriptModule, catalog, CompileOptions.Default);
		if (!compiled.Success)
			throw new BuildException(Describe(compiled, root));

		KbcModule module = compiled.Program!.Module;
		if (!module.TryFindFunction(BuildPrelude.NewWorkspace, 1, out int factory))
			throw new BuildException("встроенный модуль kern.build: нет функции __new_workspace(workspace)");
		if (!module.TryFindFunction(BuildPrelude.BuildFunction, 1, out int build))
			throw new BuildException($"{ScriptName}: не найдена функция build(workspace)");

		var interpreter = new Interpreter(module, new NativeResolver(compiled.Program.Catalog));
		KValue workspaceValue;
		try
		{
			workspaceValue = interpreter.Run(factory, [KValue.Str(root)]);
			_ = interpreter.Run(build, [workspaceValue]);
		}
		catch (KernPanic panic)
		{
			throw new BuildException($"{ScriptName}: {panic.Message}");
		}

		BuildModel workspace = BuildObjectReader.ReadWorkspace(workspaceValue, root, scriptPath, hasBuildFile: true);
		Validate(workspace);
		return new BuildSession(root, scriptPath, workspace, interpreter);
	}

	/// <summary>Настройки компиляции цели с учётом настроек рабочей области (§7a).</summary>
	public CompileOptions OptionsFor(TargetModel target)
	{
		ArgumentNullException.ThrowIfNull(target);
		CompilerSettingsModel settings = target.Compiler.IsDefault ? Workspace.Compiler : target.Compiler;
		return new CompileOptions
		{
			Optimize = OptimizeOverride ?? settings.Optimize,
			WarningsAsErrors = settings.WarningsAsErrors,
			Macros = settings.Macros,
			DebugInfo = settings.DebugInfo,
		};
	}

	private static BuildSession OpenConvention(string root)
	{
		string name = FolderName(root);
		bool hasMain =
			File.Exists(Path.Combine(root, "src", "main.kern")) ||
			File.Exists(Path.Combine(root, "main.kern"));
		var target = new TargetModel
		{
			Name = name,
			Kind = hasMain ? "app" : "lib",
			Entry = "main",
			IsConvention = true,
		};
		var workspace = new BuildModel
		{
			Root = root,
			Name = name,
			Compiler = CompilerSettingsModel.Default,
			Targets = [target],
			HasBuildFile = false,
		};
		return new BuildSession(root, null, workspace, interpreter: null);
	}

	private static void Validate(BuildModel workspace)
	{
		var targetNames = new HashSet<string>(StringComparer.Ordinal);
		foreach (TargetModel target in workspace.Targets)
		{
			if (string.IsNullOrWhiteSpace(target.Name))
				throw new BuildException($"{ScriptName}: у цели пустое имя");
			if (!targetNames.Add(target.Name))
				throw new BuildException($"{ScriptName}: цель '{target.Name}' объявлена дважды");
			if (target.Kind is not ("app" or "lib"))
				throw new BuildException(
					$"{ScriptName}: цель '{target.Name}': неизвестный вид '{target.Kind}' (ожидалось \"app\" или \"lib\")");
		}
	}

	private static string ResolveRoot(string directory)
	{
		string root;
		try
		{
			root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new BuildException($"неверный путь проекта: {directory}");
		}

		if (!Directory.Exists(root))
			throw new BuildException($"папка проекта не найдена: {directory}");
		return root;
	}

	private static string ReadScript(string path)
	{
		try
		{
			return File.ReadAllText(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new BuildException($"не удалось прочитать {path}: {ex.Message}");
		}
	}

	private static string FolderName(string root)
	{
		string name = new DirectoryInfo(root).Name;
		return string.IsNullOrEmpty(name) ? "main" : name;
	}

	/// <summary>
	/// Диагностики сценария печатаются по путям файлов проекта: ошибки встроенного прелюда
	/// означают ошибку kernc, но пользователь всё равно видит, какой файл не собрался.
	/// </summary>
	private static string Describe(CompileResult result, string root)
	{
		var lines = new List<string>();
		if (result.FailureMessage is { } failure)
			lines.Add($"ошибка: {failure}");
		lines.AddRange(result.ProjectMessages);
		lines.AddRange(result.NativeProblems.Select(problem => $"ошибка: {problem}"));
		if (lines.Count == 0)
			lines.Add($"не удалось скомпилировать {Path.Combine(root, ScriptName)}");

		return string.Join(Environment.NewLine, lines);
	}
}
