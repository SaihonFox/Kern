using Kern.Cli.Build;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc tasks [папка]</c>: печатает цели проекта и их зависимости, затем задачи
/// (<c>после: …</c> — предварительные задачи, docs/BUILDER.md §3, §4). Папка без
/// <c>build.kern</c> показывает одну цель по соглашению.
/// </summary>
internal static class TasksCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		if (!TryDirectory(args, stderr, out string directory))
			return 2;

		BuildSession session;
		try
		{
			session = BuildSession.Open(directory);
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}

		BuildModel workspace = session.Workspace;
		stdout.WriteLine(session.HasBuildFile
			? $"проект: {workspace.Name}"
			: $"проект: {workspace.Name} (по соглашению)");

		foreach (TargetModel target in workspace.Targets)
		{
			var details = new List<string> { target.Kind };
			if (target.Deps.Count > 0)
				details.Add("deps: " + string.Join(", ", target.Deps));
			if (!target.IsLibrary && !string.IsNullOrWhiteSpace(target.Entry))
				details.Add("entry: " + target.Entry);
			stdout.WriteLine($"цель {target.Name} ({string.Join(", ", details)})");
			foreach (TaskModel task in target.Tasks)
				stdout.WriteLine($"задача {target.Name}:{task.Name}");
		}

		foreach (TaskModel task in workspace.Tasks)
			stdout.WriteLine(task.After.Count > 0
				? $"задача {task.Name} (после: {string.Join(", ", task.After)})"
				: $"задача {task.Name}");

		PrintIncluded(session, stdout, "");

		stdout.WriteLine("встроенные задачи: <цель>:compile|run|exe|dll|check|dis|test, :compile, :clean");
		return 0;
	}

	private static void PrintIncluded(BuildSession session, TextWriter stdout, string prefix)
	{
		foreach (BuildSession included in session.Included)
		{
			string relative = Path.GetRelativePath(session.Root, included.Root).Replace('\\', '/');
			string id = prefix.Length == 0 ? relative : $"{prefix}/{relative}";
			stdout.WriteLine($"проект: {included.Workspace.Name} ({id})");
			foreach (TargetModel target in included.Workspace.Targets)
			{
				stdout.WriteLine($"цель {id}:{target.Name} ({target.Kind})");
				foreach (TaskModel task in target.Tasks)
					stdout.WriteLine($"задача {id}:{target.Name}:{task.Name}");
			}

			foreach (TaskModel task in included.Workspace.Tasks)
				stdout.WriteLine($"задача {id}:{task.Name}");
			PrintIncluded(included, stdout, id);
		}
	}

	private static bool TryDirectory(string[] args, TextWriter stderr, out string directory)
	{
		directory = ".";
		if (args.Length > 1 || (args.Length == 1 && args[0].StartsWith('-')))
		{
			stderr.WriteLine("ошибка: укажите папку проекта: kernc tasks [папка]");
			return false;
		}

		if (args.Length == 1)
			directory = args[0];

		if (!Directory.Exists(directory))
		{
			stderr.WriteLine($"ошибка: папка не найдена: {directory}");
			return false;
		}

		return true;
	}
}
