using Kern.Cli.Commands;
using Kern.Cli.Infrastructure;
using Kern.Vm.Execution;

namespace Kern.Cli.Build;

/// <summary>Шаг плана задачи: пользовательская задача, встроенная компиляция или очистка.</summary>
internal abstract record BuildStep(string Id);

internal sealed record UserTaskStep(string Id, TaskModel Task) : BuildStep(Id);

/// <summary>Встроенная <c>:compile</c>; <see cref="Target"/> = <c>null</c> — все цели.</summary>
internal sealed record CompileStep(string Id, TargetModel? Target) : BuildStep(Id);

/// <summary>Встроенная <c>:clean</c> — удаление out/ и dist/ проекта (как <c>kernc clean</c>).</summary>
internal sealed record CleanStep(string Id) : BuildStep(Id);

/// <summary>
/// План <c>kernc do</c> (docs/BUILDER.md §4): <c>after</c> — предварительные задачи,
/// порядок топологический, каждая задача выполняется один раз, цикл — ошибка сборки.
/// </summary>
internal static class BuildTaskPlanner
{
	private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
	{
		"compile", "run", "test", "exe", "dll", "check", "dis", "clean",
	};

	public static IReadOnlyList<BuildStep> Plan(BuildSession session, string id)
	{
		ArgumentNullException.ThrowIfNull(session);
		if (string.IsNullOrWhiteSpace(id))
			throw new BuildException("пустое имя задачи");

		var steps = new List<BuildStep>();
		var state = new Dictionary<string, int>(StringComparer.Ordinal);
		var stack = new List<string>();

		void Visit(string current)
		{
			if (state.TryGetValue(current, out int status))
			{
				if (status == 2)
					return;

				int start = stack.IndexOf(current);
				var path = stack.Skip(start).ToList();
				path.Add(current);
				throw new BuildException($"цикл в задачах: {string.Join(" -> ", path)}");
			}

			BuildStep step = Resolve(session, current);
			state[current] = 1;
			stack.Add(current);
			if (step is UserTaskStep user)
				foreach (string after in user.Task.After)
					Visit(after);
			stack.RemoveAt(stack.Count - 1);
			state[current] = 2;
			steps.Add(step);
		}

		Visit(id);
		return steps;
	}

	private static BuildStep Resolve(BuildSession session, string id)
	{
		if (id.StartsWith(':'))
			return ResolveBuiltin(id, id[1..], target: null);

		int colon = id.IndexOf(':');
		if (colon >= 0)
		{
			string targetName = id[..colon];
			string taskName = id[(colon + 1)..];
			if (targetName.Length == 0 || taskName.Length == 0 || taskName.Contains(':'))
				throw new BuildException($"неверный идентификатор задачи: '{id}' (ожидалось target:task)");

			TargetModel target = session.Workspace.FindTarget(targetName)
				?? throw new BuildException($"цель '{targetName}' не найдена; цели проекта: {TargetList(session)}");

			if (Builtins.Contains(taskName))
				return ResolveBuiltin(id, taskName, target);

			TaskModel task = target.FindTask(taskName)
				?? throw new BuildException(
					$"у цели '{targetName}' нет задачи '{taskName}'; задачи цели: {TaskList(target)}");
			return new UserTaskStep(id, task);
		}

		TaskModel? workspaceTask = session.Workspace.Tasks.FirstOrDefault(task =>
			string.Equals(task.Name, id, StringComparison.Ordinal));
		if (workspaceTask is not null)
			return new UserTaskStep(id, workspaceTask);

		throw new BuildException($"задача '{id}' не найдена; задачи проекта: {TaskList(session)}");
	}

	private static BuildStep ResolveBuiltin(
		string id,
		string name,
		TargetModel? target)
	{
		switch (name)
		{
			case "compile" or "build":
				return new CompileStep(id, target);
			case "clean":
				return new CleanStep(id);
			default:
				throw new BuildException(
					$"встроенная задача '{id}' пока не реализована; доступны: <цель>:compile, :compile, :clean");
		}
	}

	private static string TargetList(BuildSession session) =>
		session.Workspace.Targets.Count == 0
			? "нет"
			: string.Join(", ", session.Workspace.Targets.Select(target => target.Name));

	private static string TaskList(BuildSession session)
	{
		var names = new List<string>(session.Workspace.Tasks.Select(task => task.Name));
		foreach (TargetModel target in session.Workspace.Targets)
			names.AddRange(target.Tasks.Select(task => $"{target.Name}:{task.Name}"));

		return names.Count == 0 ? "нет" : string.Join(", ", names);
	}

	private static string TaskList(TargetModel target) =>
		target.Tasks.Count == 0
			? "нет"
			: string.Join(", ", target.Tasks.Select(task => task.Name));
}

/// <summary>Выполнение готового плана: печатает, что запущено, и останавливается на первой ошибке.</summary>
internal static class BuildTaskRunner
{
	public static int Run(
		IReadOnlyList<BuildStep> steps,
		BuildSession session,
		TextWriter stdout,
		TextWriter stderr)
	{
		ArgumentNullException.ThrowIfNull(steps);
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(stdout);
		ArgumentNullException.ThrowIfNull(stderr);

		foreach (BuildStep step in steps)
		{
			stdout.WriteLine($"задача {step.Id}");
			switch (step)
			{
				case UserTaskStep user:
					if (RunUserTask(session, user, stderr) is { } failure)
						return failure;
					break;
				case CompileStep compile:
					if (RunCompile(session, compile, stdout, stderr) is { } compileFailure)
						return compileFailure;
					break;
				case CleanStep:
					int cleanCode = CleanCommand.Run([session.Root], stdout, stderr);
					if (cleanCode != 0)
						return cleanCode;
					break;
			}
		}

		return 0;
	}

	private static int? RunUserTask(BuildSession session, UserTaskStep step, TextWriter stderr)
	{
		if (session.Interpreter is not { } interpreter)
		{
			stderr.WriteLine($"ошибка: задача '{step.Id}' требует build.kern, но проекта по соглашению нет");
			return 1;
		}

		try
		{
			_ = interpreter.RunValue(step.Task.Action);
			return null;
		}
		catch (KernPanic panic)
		{
			stderr.WriteLine($"ошибка выполнения задачи '{step.Id}': {panic.Message}");
			return 1;
		}
	}

	private static int? RunCompile(
		BuildSession session,
		CompileStep step,
		TextWriter stdout,
		TextWriter stderr)
	{
		List<BuildTargetResult> results;
		try
		{
			results = step.Target is { } target
				? [BuildTargetCompiler.Compile(session, target, output: null)]
				: [.. BuildTargetCompiler.BuildAll(session, output: null)];
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}

		foreach (BuildTargetResult result in results)
			SourceCompiler.Report(result.Compile, stderr);
		if (results.Any(result => !result.Compile.Success))
			return 1;

		foreach (BuildTargetResult result in results)
			if (result.OutputPath is { } path)
				stdout.WriteLine(path);
		return null;
	}
}
