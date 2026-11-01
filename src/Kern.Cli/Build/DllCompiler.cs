using Kern.Bytecode;
using Kern.Cli.Infrastructure;
using Kern.Cli.Kbc;

namespace Kern.Cli.Build;

/// <summary>
/// <c>kernc dll</c> и встроенная задача <c>&lt;цель&gt;:dll</c> (spec round 25): компилирует
/// цель в <c>.kbc</c>, вырезает точку входа и встраивает интерфейсные исходники
/// (<c>kern.stub</c>), чтобы потребитель мог импортировать библиотеку обычным <c>use</c>.
/// </summary>
internal static class DllCompiler
{
	public static string Build(BuildSession session, TargetModel? target, string? output)
	{
		ArgumentNullException.ThrowIfNull(session);
		TargetModel selected = target ?? SelectTarget(session);
		BuildTargetResult compiled = BuildTargetCompiler.Compile(session, selected, output: null, write: false);
		if (!compiled.Compile.Success)
			throw new BuildException(Describe(compiled.Compile, selected));

		KbcModule module = compiled.Compile.Program!.Module;
		module.EntryPoint = -1;
		IReadOnlyList<(string Path, string Text)> sources = Sources(session, selected);
		var expanded = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var (path, text) in compiled.Compile.ExpandedFiles ?? sources)
			expanded[path] = text;
		foreach (var (path, text) in sources)
		{
			string source = expanded.TryGetValue(path, out string? expandedText) ? expandedText : text;
			string stub = StubGenerator.Generate(source, path);
			module.Attributes.Add(new AttributeUse(
				"kern.stub",
				KConst.Str(path),
				KConst.Str(ModuleName(path)),
				KConst.Str(stub)));
		}

		string result = output ?? Path.Combine(session.Root, "out", selected.Name + ".kbc");
		Write(module, result);
		return result;
	}

	private static TargetModel SelectTarget(BuildSession session)
	{
		IReadOnlyList<TargetModel> libraries = [.. session.Workspace.Targets.Where(item => item.IsLibrary)];
		if (libraries.Count == 1)
			return libraries[0];
		if (libraries.Count > 1)
			throw new BuildException(
				$"в build.kern несколько lib-целей ({string.Join(", ", libraries.Select(item => item.Name))}): " +
				"dll не знает, какую выбрать");
		if (session.Workspace.Targets.Count == 1)
			return session.Workspace.Targets[0];
		throw new BuildException("в build.kern нет lib-цели для dll");
	}

	private static IReadOnlyList<(string Path, string Text)> Sources(BuildSession session, TargetModel target)
	{
		if (!target.IsConvention)
			return [.. BuildTargetSources.Collect(session, target).Select(file => (file.ModulePath, file.Text))];

		if (!ProjectSources.TryLoad(session.Root, out ProjectSources? project, out string? error))
			throw new BuildException(error ?? "не удалось прочитать проект");
		return [.. project!.Files];
	}

	private static string ModuleName(string path)
	{
		string withoutExtension = Path.ChangeExtension(path, null).Replace('\\', '/');
		string normalized = withoutExtension.Replace('/', '.');
		return normalized.StartsWith("src.", StringComparison.Ordinal) ? normalized[4..] : normalized;
	}

	private static string Describe(CompileResult result, TargetModel target)
	{
		var lines = new List<string>();
		if (result.FailureMessage is { } failure)
			lines.Add(failure);
		lines.AddRange(result.ProjectMessages);
		lines.AddRange(result.NativeProblems);
		return lines.Count > 0
			? string.Join(Environment.NewLine, lines)
			: $"цель '{target.Name}' не скомпилировалась";
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
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new BuildException($"не удалось записать {path}: {exception.Message}");
		}
	}
}
