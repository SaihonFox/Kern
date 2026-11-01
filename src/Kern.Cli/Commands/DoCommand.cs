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
		if (args.Length is 0 or > 2 || args[0].StartsWith('-'))
		{
			stderr.WriteLine("ошибка: укажите задачу: kernc do <задача> [папка]");
			return 2;
		}

		string task = args[0];
		string directory = args.Length == 2 ? args[1] : ".";
		if (!Directory.Exists(directory))
		{
			stderr.WriteLine($"ошибка: папка не найдена: {directory}");
			return 2;
		}

		try
		{
			BuildSession session = BuildSession.Open(directory);
			IReadOnlyList<BuildStep> plan = BuildTaskPlanner.Plan(session, task);
			return BuildTaskRunner.Run(plan, session, stdout, stderr);
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}
	}
}
