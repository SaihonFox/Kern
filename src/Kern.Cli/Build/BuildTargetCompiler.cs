using Kern.Binding.Catalog;
using Kern.Bytecode;
using Kern.Cli.Infrastructure;
using Kern.Runtime.Core;

namespace Kern.Cli.Build;

/// <summary>Результат сборки одной цели: диагностики, готовый модуль и записанный путь (если есть).</summary>
internal sealed record BuildTargetResult(TargetModel Target, CompileResult Compile, string? OutputPath);

/// <summary>
/// Сборка цели <c>build.kern</c> (docs/BUILDER.md §3, задача <c>compile</c>): исходники цели
/// и её зависимостей компилируются одним модулем с именем цели; app-цель пишется в
/// <c>&lt;root&gt;/out/&lt;цель&gt;.kbc</c>. Папка без <c>build.kern</c> идёт прежним
/// соглашением §2 через <see cref="ProjectSources"/>.
/// </summary>
internal static class BuildTargetCompiler
{
	private static readonly NativeCatalog CoreCatalog = NativeCatalog.Scan(typeof(CoreModule).Assembly);

	/// <summary>Собрать все цели в порядке зависимостей; при первой ошибке остановиться.</summary>
	public static IReadOnlyList<BuildTargetResult> BuildAll(BuildSession session, string? output)
	{
		ArgumentNullException.ThrowIfNull(session);

		IReadOnlyList<TargetModel> targets = BuildGraph.TopologicalOrder(session.Workspace);
		if (targets.Count == 0)
			throw new BuildException("в build.kern не объявлено ни одной цели");
		if (output is not null && targets[0] is { IsConvention: false } &&
			(targets.Count != 1 || targets[0].IsLibrary))
			throw new BuildException("параметр -o применим, только если в build.kern одна app-цель");

		var results = new List<BuildTargetResult>(targets.Count);
		foreach (TargetModel target in targets)
		{
			BuildTargetResult result = Compile(session, target, output);
			results.Add(result);
			if (!result.Compile.Success)
				break;
		}

		return results;
	}

	/// <summary>
	/// Скомпилировать единственную app-цель без записи <c>.kbc</c> — путь <c>run</c>/<c>check</c>.
	/// Несколько app-целей неоднозначны: выбор делает пользователь командой <c>do</c>/<c>build</c>.
	/// </summary>
	public static BuildTargetResult CompileApp(BuildSession session)
	{
		ArgumentNullException.ThrowIfNull(session);

		var applications = session.Workspace.Targets.Where(target => !target.IsLibrary).ToList();
		if (applications.Count == 0)
			throw new BuildException("в build.kern нет app-цели для run/check");
		if (applications.Count > 1)
			throw new BuildException(
				$"в build.kern несколько app-целей ({string.Join(", ", applications.Select(target => target.Name))}): " +
				"run/check не знают, какую выбрать");
		return Compile(session, applications[0], output: null, write: false);
	}

	/// <summary>Скомпилировать одну цель; app-цель (и проект по соглашению) ещё и записать в .kbc.</summary>
	public static BuildTargetResult Compile(
		BuildSession session,
		TargetModel target,
		string? output,
		bool write = true)
	{
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(target);

		if (target.IsConvention)
			return CompileConvention(session, target, output, write);

		IReadOnlyList<BuildSourceFile> files = BuildTargetSources.Collect(session, target);
		CompileResult compiled = SourceCompiler.CompileFiles(
			[.. files.Select(file => (file.ModulePath, file.Text))],
			target.Name,
			CoreCatalog,
			session.OptionsFor(target));
		compiled = WithProjectPaths(compiled, files, session.Root);
		if (!compiled.Success)
			return new BuildTargetResult(target, compiled, null);

		ValidateApp(target, compiled, files);

		// Библиотека участвует в сборке зависимых целей своими исходниками; отдельный .kbc
		// для неё появится в срезе dll, поэтому сейчас пишем его только по явному -o.
		if (target.IsLibrary && output is null)
			return new BuildTargetResult(target, compiled, null);
		if (!write)
			return new BuildTargetResult(target, compiled, null);

		string path = output ?? Path.Combine(session.Root, "out", target.Name + ".kbc");
		Write(compiled.Program!.Module, path);
		return new BuildTargetResult(target, compiled, path);
	}

	private static BuildTargetResult CompileConvention(
		BuildSession session,
		TargetModel target,
		string? output,
		bool write)
	{
		if (!ProjectSources.TryLoad(session.Root, out ProjectSources? project, out string? error))
			return new BuildTargetResult(target, new CompileResult { FailureMessage = error }, null);

		CompileResult compiled = SourceCompiler.Compile(project!, session.OptionsFor(target));
		if (!compiled.Success)
			return new BuildTargetResult(target, compiled, null);
		if (!write)
			return new BuildTargetResult(target, compiled, null);

		string path = project!.OutputKbcPath(output);
		Write(compiled.Program!.Module, path);
		return new BuildTargetResult(target, compiled, path);
	}

	/// <summary>Проверка app-цели: модуль входа существует и содержит <c>fun main</c> (§2, §3).</summary>
	private static void ValidateApp(
		TargetModel target,
		CompileResult compiled,
		IReadOnlyList<BuildSourceFile> files)
	{
		if (target.IsLibrary)
			return;

		if (!string.IsNullOrWhiteSpace(target.Entry))
		{
			string expected = target.Entry.Replace('.', '/');
			bool found = files.Any(file =>
				string.Equals(Path.ChangeExtension(file.ModulePath, null), expected, StringComparison.Ordinal));
			if (!found)
				throw new BuildException(
					$"цель '{target.Name}': точка входа '{target.Entry}' не найдена в исходниках");
		}

		if (compiled.Program is null || compiled.Program.Module.EntryPoint < 0)
			throw new BuildException($"цель '{target.Name}': в точке входа нет fun main");
	}

	/// <summary>
	/// Диагностики цели печатаются по путям проекта (<c>app/src/main.kern</c>), а не по
	/// путям модулей: у одного модуля-файла нет каталога источника.
	/// </summary>
	private static CompileResult WithProjectPaths(
		CompileResult result,
		IReadOnlyList<BuildSourceFile> files,
		string root)
	{
		if (result.ProjectMessages.Count == 0)
			return result;

		var paths = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (BuildSourceFile file in files)
			paths[file.ModulePath] = Path.GetRelativePath(root, file.FullPath).Replace('\\', '/');

		var messages = new List<string>(result.ProjectMessages.Count);
		foreach (string message in result.ProjectMessages)
			messages.Add(RewritePath(message, paths));

		return new CompileResult
		{
			Program = result.Program,
			Diagnostics = result.Diagnostics,
			NativeProblems = result.NativeProblems,
			ProjectMessages = messages,
			ProjectDiagnostics = result.ProjectDiagnostics,
			FailureMessage = result.FailureMessage,
			Source = result.Source,
			Sources = result.Sources,
		};
	}

	private static string RewritePath(string message, IReadOnlyDictionary<string, string> paths)
	{
		foreach (var (modulePath, projectPath) in paths)
			if (message.StartsWith(modulePath + ":", StringComparison.Ordinal))
				return projectPath + message[modulePath.Length..];
		return message;
	}

	private static void Write(KbcModule module, string path)
	{
		try
		{
			string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
			if (directory is { Length: > 0 })
				Directory.CreateDirectory(directory);
			File.WriteAllBytes(path, KbcWriter.Write(module));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new BuildException($"не удалось записать {path}: {ex.Message}");
		}
	}
}
