using Kern.Cli.Build;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc do &lt;задача&gt; [папка]</c>: выполняет задачу пользовательского
/// <c>build.kern</c> (docs/BUILDER.md §4). Задача — имя из <c>w.task</c>, <c>цель:задача</c>
/// из <c>t.task</c> или встроенная <c>:compile</c>/<c>:clean</c>. Сначала выполняются
/// предварительные задачи из <c>after</c>, порядок топологический, цикл — ошибка.
/// </summary>
internal static class DoCommand
{
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		string? profile = null;
		var positional = new List<string>();
		for (var index = 0; index < args.Length; index++)
		{
			if (args[index] == "-p")
			{
				if (index + 1 >= args.Length)
				{
					stderr.WriteLine("ошибка: параметр -p требует имя профиля");
					return 2;
				}

				profile = args[++index];
				continue;
			}

			if (args[index].StartsWith('-'))
			{
				stderr.WriteLine($"ошибка: неизвестный параметр '{args[index]}'");
				return 2;
			}

			positional.Add(args[index]);
		}

		if (positional.Count is 0 or > 2)
		{
			stderr.WriteLine("ошибка: укажите задачу: kernc do <задача> [папка]");
			return 2;
		}

		string task = positional[0];
		string directory = positional.Count == 2 ? positional[1] : ".";
		if (!Directory.Exists(directory))
		{
			stderr.WriteLine($"ошибка: папка не найдена: {directory}");
			return 2;
		}

		try
		{
			BuildSession session = BuildSession.Open(directory);
			session.ProfileName = profile;
			(BuildSession targetSession, string targetTask) = ResolveTask(session, task);
			IReadOnlyList<BuildStep> plan = BuildTaskPlanner.Plan(targetSession, targetTask);
			return BuildTaskRunner.Run(plan, targetSession, stdout, stderr);
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}
	}

	/// <summary>
	/// <c>project:target:task</c> для подпроекта w.include; локальные ids
	/// (<c>task</c>, <c>target:task</c>) остаются как есть.
	/// </summary>
	private static (BuildSession Session, string Task) ResolveTask(BuildSession root, string id)
	{
		int taskColon = id.LastIndexOf(':');
		int targetColon = taskColon > 0 ? id.LastIndexOf(':', taskColon - 1) : -1;
		if (targetColon <= 0)
			return (root, id);

		string project = id[..targetColon];
		BuildSession? included = root.FindIncluded(project);
		if (included is null)
			return (root, id);
		return (included, id[(targetColon + 1)..]);
	}
}
