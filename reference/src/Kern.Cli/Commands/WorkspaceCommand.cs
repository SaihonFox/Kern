using Kern.Cli.Build;

namespace Kern.Cli.Commands;

/// <summary>Команды в папке с build.kern: сопоставляет `build/run/test/exe/clean/tasks/do` с задачами графа.</summary>
internal static class WorkspaceCommand
{
	public static int Run(string command, string root, List<string> args, string[] programArgs, int opt)
	{
		var script = BuildScript.Load(root);
		if (script is null) return 1;
		var runner = new BuildRunner(script, opt);

		switch (command)
		{
			case "tasks":
				Console.Out.WriteLine($"Рабочая область '{script.Workspace.Name}' {script.Workspace.Version} (по умолчанию: {runner.DefaultTask})");
				foreach (var (name, description) in runner.Tasks) Console.Out.WriteLine($"  {name,-24} {description}");
				return 0;

			case "do":
				return runner.Execute(args.Count > 0 ? args : [runner.DefaultTask], programArgs);

			case "build":
				return runner.Execute(args.Count > 0 ? args : [runner.DefaultTask], programArgs);

			case "test":
			case "clean":
				return runner.Execute([args.Count > 0 ? args[0] + ":" + command : command], programArgs);

			case "run":
			case "exe":
			{
				string? target = args.FirstOrDefault();
				if (target is null)
				{
					var apps = runner.AppTargets.ToList();
					if (apps.Count != 1)
					{
						Console.Error.WriteLine($"error: укажите цель: kernc {command} . <цель> (исполняемые цели: {string.Join(", ", apps)})");
						return 2;
					}
					target = apps[0];
				}
				return runner.Execute([$"{target}:{command}"], programArgs);
			}
			default:
				return 2;
		}
	}
}
