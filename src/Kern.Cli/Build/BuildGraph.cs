namespace Kern.Cli.Build;

/// <summary>
/// Граф целей и задач (docs/BUILDER.md §3, §4): порядок «зависимости раньше» и понятные
/// русские ошибки для неизвестной зависимости и цикла.
/// </summary>
internal static class BuildGraph
{
	/// <summary>Все цели проекта в порядке объявления зависимостей (deps раньше зависимых).</summary>
	public static IReadOnlyList<TargetModel> TopologicalOrder(BuildModel workspace)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		if (workspace.Targets.Count == 0)
			return [];

		var ordered = new List<TargetModel>();
		var state = new Dictionary<TargetModel, int>(ReferenceEqualityComparer.Instance);
		var stack = new List<TargetModel>();
		foreach (TargetModel target in workspace.Targets)
			Visit(workspace, target, state, stack, ordered);
		return ordered;
	}

	/// <summary>Цель и все её зависимости исходниками (рекурсивно, без повторов).</summary>
	public static IReadOnlyList<TargetModel> Closure(BuildModel workspace, TargetModel target)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(target);

		var ordered = new List<TargetModel>();
		var state = new Dictionary<TargetModel, int>(ReferenceEqualityComparer.Instance);
		var stack = new List<TargetModel>();
		Visit(workspace, target, state, stack, ordered);
		return ordered;
	}

	private static void Visit(
		BuildModel workspace,
		TargetModel target,
		Dictionary<TargetModel, int> state,
		List<TargetModel> stack,
		List<TargetModel> ordered)
	{
		if (state.TryGetValue(target, out int status))
		{
			if (status == 2)
				return;

			// status == 1: цель уже на стеке — цикл; показываем путь от повторной цели.
			int start = stack.IndexOf(target);
			var path = stack.Skip(start).Select(item => item.Name).ToList();
			path.Add(target.Name);
			throw new BuildException($"цикл зависимостей целей: {string.Join(" -> ", path)}");
		}

		state[target] = 1;
		stack.Add(target);
		foreach (string dependency in target.Deps)
		{
			// `project:target` — зависимость на подпроект w.include; её исходники
			// подмешивает BuildTargetSources, в графе текущего проекта её нет.
			if (dependency.Contains(':', StringComparison.Ordinal))
				continue;
			TargetModel? resolved = workspace.FindTarget(dependency);
			if (resolved is null)
				throw new BuildException($"цель '{target.Name}': зависимость '{dependency}' не найдена");
			Visit(workspace, resolved, state, stack, ordered);
		}

		stack.RemoveAt(stack.Count - 1);
		state[target] = 2;
		ordered.Add(target);
	}
}
