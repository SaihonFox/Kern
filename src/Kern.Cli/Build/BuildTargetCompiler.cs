using System.Security.Cryptography;
using System.Text;
using Kern.Binding.Catalog;
using Kern.Bytecode;
using Kern.Cli.Infrastructure;
using Kern.Cli.Kbc;

namespace Kern.Cli.Build;

/// <summary>Результат сборки одной цели: диагностики, готовый модуль и записанный путь (если есть).</summary>
internal sealed record BuildTargetResult(TargetModel Target, CompileResult Compile, string? OutputPath)
{
	/// <summary>Сборка пропущена: хэш исходников и настроек не изменился (BUILDER.md §4).</summary>
	public bool UpToDate { get; init; }
}

/// <summary>
/// Сборка цели <c>build.kern</c> (docs/BUILDER.md §3, задача <c>compile</c>): исходники цели
/// и её зависимостей компилируются одним модулем с именем цели; app-цель пишется в
/// <c>&lt;root&gt;/out/&lt;цель&gt;.kbc</c>, lib-цель — туда же без точки входа и с
/// интерфейсными заглушками. Папка без <c>build.kern</c> идёт прежним
/// соглашением §2 через <see cref="ProjectSources"/>.
/// </summary>
internal static class BuildTargetCompiler
{
	/// <summary>Собрать все цели в порядке зависимостей; при первой ошибке остановиться.</summary>
	public static IReadOnlyList<BuildTargetResult> BuildAll(BuildSession session, string? output)
	{
		ArgumentNullException.ThrowIfNull(session);

		IReadOnlyList<TargetModel> targets = BuildGraph.TopologicalOrder(session.Workspace);
		if (targets.Count == 0)
			throw new BuildException("в build.kern не объявлено ни одной цели");
		if (output is not null && targets[0] is { IsConvention: false } && targets.Count != 1)
			throw new BuildException("параметр -o применим, только если в build.kern одна цель");

		var results = new List<BuildTargetResult>(targets.Count);
		foreach (TargetModel target in targets)
		{
			BuildTargetResult result = Compile(session, target, output);
			results.Add(result);
			if (!result.Compile.Success && !result.UpToDate)
				break;
		}

		foreach (BuildSession included in session.Included)
			results.AddRange(BuildAll(included, output: null));

		return results;
	}

	/// <summary>Проверить все цели без записи (<c>:check</c>).</summary>
	public static IReadOnlyList<BuildTargetResult> BuildAllCheck(BuildSession session)
	{
		ArgumentNullException.ThrowIfNull(session);
		IReadOnlyList<TargetModel> targets = BuildGraph.TopologicalOrder(session.Workspace);
		if (targets.Count == 0)
			throw new BuildException("в build.kern не объявлено ни одной цели");
		var results = new List<BuildTargetResult>(targets.Count);
		foreach (TargetModel target in targets)
		{
			BuildTargetResult result = Compile(session, target, output: null, write: false);
			results.Add(result);
			if (!result.Compile.Success)
				break;
		}

		foreach (BuildSession included in session.Included)
			results.AddRange(BuildAllCheck(included));

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

		IReadOnlyList<KbcDependency> dependencies = KbcDependency.Load(session.Root, target.KbcPaths);
		IReadOnlyList<BuildSourceFile> files = BuildTargetSources.Collect(session, target);
		files = WithStubs(files, dependencies);
		string path = output ?? Path.Combine(session.Root, "out", target.Name + ".kbc");
		string hash = Hash(files.Select(file => (file.ModulePath, file.Text)), session.OptionsFor(target), target, session.Root);
		if (write && output is null && IsUpToDate(path, hash))
			return new BuildTargetResult(target, new CompileResult(), path) { UpToDate = true };

		CompileResult compiled = SourceCompiler.CompileFiles(
			[.. files.Select(file => (file.ModulePath, file.Text))],
			target.Name,
			session.CatalogFor(target),
			session.OptionsFor(target),
			[.. dependencies.Select(dependency => dependency.Module)]);
		compiled = WithProjectPaths(compiled, files, session.Root);
		if (!compiled.Success)
			return new BuildTargetResult(target, compiled, null);

		ValidateApp(target, compiled, files);

		if (target.IsLibrary)
		{
			// Как библиотека в C#: проект вида "lib" по умолчанию даёт готовый .kbc без
			// точки входа и с интерфейсными заглушками, поэтому его можно подключить
			// через t.kbc(...) без отдельной команды dll.
			compiled.Program!.Module.EntryPoint = -1;
			AddStubs(compiled, files);
		}
		if (!write)
			return new BuildTargetResult(target, compiled, null);

		Write(compiled.Program!.Module, path);
		SaveHash(path, hash);
		return new BuildTargetResult(target, compiled, path);
	}

	/// <summary>
	/// Встроить интерфейсные исходники (<c>kern.stub</c>) в библиотечный модуль, чтобы
	/// потребитель мог импортировать его обычным <c>use</c> (spec round 25).
	/// </summary>
	private static void AddStubs(CompileResult compiled, IReadOnlyList<BuildSourceFile> files)
	{
		var expanded = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var (path, text) in compiled.ExpandedFiles ?? [])
			expanded[path] = text;

		foreach (BuildSourceFile file in files)
		{
			string source = expanded.TryGetValue(file.ModulePath, out string? expandedText)
				? expandedText
				: file.Text;
			string stub = StubGenerator.Generate(source, file.ModulePath);
			compiled.Program!.Module.Attributes.Add(new AttributeUse(
				"kern.stub",
				KConst.Str(file.ModulePath),
				KConst.Str(ModuleName(file.ModulePath)),
				KConst.Str(stub)));
		}
	}

	private static string ModuleName(string path)
	{
		string withoutExtension = Path.ChangeExtension(path, null).Replace('\\', '/');
		string normalized = withoutExtension.Replace('/', '.');
		return normalized.StartsWith("src.", StringComparison.Ordinal) ? normalized[4..] : normalized;
	}

	private static IReadOnlyList<BuildSourceFile> WithStubs(		IReadOnlyList<BuildSourceFile> files,
		IReadOnlyList<KbcDependency> dependencies)
	{
		if (dependencies.Count == 0)
			return files;

		var result = new List<BuildSourceFile>(files);
		var paths = new HashSet<string>(files.Select(file => file.ModulePath), StringComparer.Ordinal);
		foreach (KbcDependency dependency in dependencies)
			foreach (StubSource stub in dependency.Stubs)
			{
				if (!paths.Add(stub.Path))
					throw new BuildException(
						$"модуль '{stub.Path}' из .kbc-зависимости конфликтует с исходником проекта");
				result.Add(new BuildSourceFile(stub.Path, $"{dependency.Path}:{stub.Path}", stub.Text));
			}

		result.Sort((left, right) => string.CompareOrdinal(left.ModulePath, right.ModulePath));
		return result;
	}

	private static BuildTargetResult CompileConvention(
		BuildSession session,
		TargetModel target,
		string? output,
		bool write)
	{
		if (!ProjectSources.TryLoad(session.Root, out ProjectSources? project, out string? error))
			return new BuildTargetResult(target, new CompileResult { FailureMessage = error }, null);

		CompileOptions options = session.OptionsFor(target);
		string hash = Hash(
			project!.Files.Select(file => (file.Path, file.Text)),
			options,
			target,
			session.Root);
		string path = project.OutputKbcPath(output);
		if (write && output is null && IsUpToDate(path, hash))
			return new BuildTargetResult(target, new CompileResult(), path) { UpToDate = true };

		CompileResult compiled = SourceCompiler.Compile(project, options);
		if (!compiled.Success)
			return new BuildTargetResult(target, compiled, null);
		if (!write)
			return new BuildTargetResult(target, compiled, null);

		Write(compiled.Program!.Module, path);
		SaveHash(path, hash);
		return new BuildTargetResult(target, compiled, path);
	}

	/// <summary>Хэш исходников, зависимостей (они уже в списке), настроек, цели и версии kernc.</summary>
	private static string Hash(
		IEnumerable<(string Path, string Text)> files,
		CompileOptions options,
		TargetModel target,
		string root)
	{
		var builder = new StringBuilder();
		builder.Append(KernCli.Version).Append('|')
			.Append(target.Name).Append('|')
			.Append(target.Kind).Append('|')
			.Append(target.Entry ?? "").Append('|')
			.Append(options.Optimize).Append('|')
			.Append(options.WarningsAsErrors).Append('|')
			.Append(options.Macros).Append('|')
			.Append(options.DebugInfo).Append('|');
		foreach (var (path, text) in files)
			builder.Append(path).Append('\0').Append(text).Append('\0');

		// `.kbc`-зависимости: их содержимое меняет линковку, поэтому входят в хэш (§4).
		foreach (string dependency in target.KbcPaths.OrderBy(path => path, StringComparer.Ordinal))
		{
			string full = Path.IsPathRooted(dependency)
				? dependency
				: Path.GetFullPath(Path.Combine(root, dependency));
			builder.Append("kbc:").Append(dependency).Append('\0');
			try
			{
				builder.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full)))).Append('\0');
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				builder.Append("missing").Append('\0');
			}
		}

		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
	}

	private static bool IsUpToDate(string path, string hash)
	{
		string hashPath = path + ".hash";
		if (!File.Exists(path) || !File.Exists(hashPath))
			return false;
		try
		{
			return string.Equals(File.ReadAllText(hashPath).Trim(), hash, StringComparison.Ordinal);
		}
		catch (IOException)
		{
			return false;
		}
	}

	private static void SaveHash(string path, string hash)
	{
		try
		{
			File.WriteAllText(path + ".hash", hash);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new BuildException($"не удалось записать хэш {path}.hash: {exception.Message}");
		}
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
