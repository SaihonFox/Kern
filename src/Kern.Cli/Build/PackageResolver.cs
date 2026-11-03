namespace Kern.Cli.Build;

/// <summary>
/// Подключение пакетов из <c>t.pkg</c> (docs/BUILDER.md §8): скачать/распаковать,
/// затем использовать как <c>w.include</c> (проект с <c>build.kern</c>) или <c>t.kbc</c>.
/// </summary>
internal static class PackageResolver
{
	public sealed record Result(
		BuildModel Workspace,
		IReadOnlyDictionary<string, BuildSession> Projects);

	/// <param name="open">Открыть проект пакета (тот же <see cref="BuildSession.Open"/>, чтобы
	/// работали вложенные include/пакеты и защита от циклов).</param>
	public static Result Resolve(
		BuildModel workspace,
		PackageFetcher fetcher,
		Func<string, BuildSession> open)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(fetcher);
		ArgumentNullException.ThrowIfNull(open);

		if (workspace.Targets.All(target => target.Packages.Count == 0))
			return new Result(workspace, new Dictionary<string, BuildSession>(StringComparer.Ordinal));

		var projects = new Dictionary<string, BuildSession>(StringComparer.Ordinal);
		var targets = new List<TargetModel>(workspace.Targets.Count);
		foreach (TargetModel target in workspace.Targets)
		{
			var dependencies = new List<string>(target.Deps);
			var kbcPaths = new List<string>(target.KbcPaths);
			var names = new HashSet<string>(StringComparer.Ordinal);
			foreach (PackageModel package in target.Packages)
			{
				if (!names.Add(package.Name))
					throw new BuildException(
						$"цель '{target.Name}': пакет '{package.Name}' указан дважды");

				string root = fetcher.Fetch(package);
				if (File.Exists(Path.Combine(root, "build.kern")))
				{
					BuildSession session;
					if (projects.TryGetValue(package.Name, out BuildSession? existing))
					{
						if (!string.Equals(existing.Root, root, StringComparison.OrdinalIgnoreCase))
							throw new BuildException(
								$"пакет '{package.Name}' уже подключён из другого архива");
						session = existing;
					}
					else
					{
						session = open(root);
						projects[package.Name] = session;
					}
					string targetName = ChooseTarget(session, package);
					string dependency = package.Name + ":" + targetName;
					if (!dependencies.Contains(dependency, StringComparer.Ordinal))
						dependencies.Add(dependency);
					continue;
				}

				string? kbc = FindKbc(root);
				if (kbc is null)
					throw new BuildException(
						$"пакет '{package.Name}': в архиве нет build.kern или .kbc");
				kbcPaths.Add(kbc);
			}

			targets.Add(target with { Deps = dependencies, KbcPaths = kbcPaths });
		}

		var updated = new BuildModel
		{
			Root = workspace.Root,
			Name = workspace.Name,
			Version = workspace.Version,
			Compiler = workspace.Compiler,
			Includes = workspace.Includes,
			LibPaths = workspace.LibPaths,
			Profiles = workspace.Profiles,
			Targets = targets,
			Tasks = workspace.Tasks,
			HasBuildFile = workspace.HasBuildFile,
		};
		return new Result(updated, projects);
	}

	/// <summary>
	/// Цель пакета: единственная цель, затем цель с именем пакета, затем единственная lib-цель.
	/// </summary>
	private static string ChooseTarget(BuildSession session, PackageModel package)
	{
		IReadOnlyList<TargetModel> targets = session.Workspace.Targets;
		if (targets.Count == 0)
			throw new BuildException($"пакет '{package.Name}': в build.kern нет целей");
		if (targets.Count == 1)
			return targets[0].Name;

		TargetModel? named = targets.FirstOrDefault(target =>
			string.Equals(target.Name, package.Name, StringComparison.Ordinal));
		if (named is not null)
			return named.Name;

		TargetModel[] libraries = [.. targets.Where(target => target.IsLibrary)];
		if (libraries.Length == 1)
			return libraries[0].Name;

		throw new BuildException(
			$"пакет '{package.Name}': несколько целей ({string.Join(", ", targets.Select(target => target.Name))}); " +
			"нужна цель с именем пакета или единственная lib-цель");
	}

	/// <summary>Первый <c>.kbc</c> в корне пакета или в его <c>out/</c>.</summary>
	private static string? FindKbc(string root)
	{
		string? direct = Directory
			.EnumerateFiles(root, "*.kbc", SearchOption.TopDirectoryOnly)
			.OrderBy(path => path, StringComparer.Ordinal)
			.FirstOrDefault();
		if (direct is not null)
			return direct;

		string output = Path.Combine(root, "out");
		return Directory.Exists(output)
			? Directory.EnumerateFiles(output, "*.kbc", SearchOption.TopDirectoryOnly)
				.OrderBy(path => path, StringComparer.Ordinal)
				.FirstOrDefault()
			: null;
	}
}
