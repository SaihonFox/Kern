using Kern.Bytecode;
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

/// <summary>Встроенная <c>:check</c> — компиляция без записи.</summary>
internal sealed record CheckStep(string Id, TargetModel? Target) : BuildStep(Id);

/// <summary>Встроенная <c>:run</c> — собрать app-цель и выполнить её.</summary>
internal sealed record RunStep(string Id, TargetModel? Target) : BuildStep(Id);

/// <summary>Встроенная <c>:exe</c> — собрать dist/&lt;имя&gt;[.exe].</summary>
internal sealed record ExeStep(string Id, TargetModel? Target) : BuildStep(Id);

/// <summary>Встроенная <c>:dist</c> — переносимый каталог dist/&lt;имя&gt;/.</summary>
internal sealed record DistStep(string Id, TargetModel? Target) : BuildStep(Id);

/// <summary>Встроенная <c>:dll</c> — собрать .kbc с интерфейсными исходниками.</summary>
internal sealed record DllStep(string Id, TargetModel? Target) : BuildStep(Id);

/// <summary>Встроенная <c>:dis</c> — листинг байткода цели.</summary>
internal sealed record DisStep(string Id, TargetModel? Target) : BuildStep(Id);

/// <summary>Встроенная <c>:test</c> — выполнить <c>#[test]</c>-функции.</summary>
internal sealed record TestStep(string Id, TargetModel? Target) : BuildStep(Id);

/// <summary>
/// План <c>kernc do</c> (docs/BUILDER.md §4): <c>after</c> — предварительные задачи,
/// порядок топологический, каждая задача выполняется один раз, цикл — ошибка сборки.
/// </summary>
internal static class BuildTaskPlanner
{
	private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
	{
		"compile", "run", "test", "exe", "dist", "dll", "check", "dis", "clean",
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
			case "check":
				return new CheckStep(id, target);
			case "run":
				return new RunStep(id, target);
			case "exe":
				return new ExeStep(id, target);
			case "dist":
				return new DistStep(id, target);
			case "dll":
				return new DllStep(id, target);
			case "dis":
				return new DisStep(id, target);
			case "test":
				return new TestStep(id, target);
			default:
				throw new BuildException(
					$"встроенная задача '{id}' пока не реализована; доступны: <цель>:compile, :compile, " +
					"<цель>:run, <цель>:exe, <цель>:dist, <цель>:dll, <цель>:check, <цель>:dis, <цель>:test, :clean");
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
				case CheckStep check:
					if (RunCheck(session, check, stderr) is { } checkFailure)
						return checkFailure;
					break;
				case RunStep run:
					if (RunProgramStep(session, run, stderr) is { } runFailure)
						return runFailure;
					break;
				case ExeStep exe:
					if (RunExe(session, exe, stdout, stderr) is { } exeFailure)
						return exeFailure;
					break;
				case DistStep dist:
					if (RunDist(session, dist, stdout, stderr) is { } distFailure)
						return distFailure;
					break;
				case DllStep dll:
					if (RunDll(session, dll, stdout, stderr) is { } dllFailure)
						return dllFailure;
					break;
				case DisStep dis:
					if (RunDis(session, dis, stdout, stderr) is { } disFailure)
						return disFailure;
					break;
				case TestStep test:
					if (TestRunner.Run(session, test.Target, stdout, stderr) is var testCode && testCode != 0)
						return testCode;
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
			var applied = new List<(string Name, string? Old)>();
			foreach (var (name, value) in session.EnvironmentFor())
			{
				applied.Add((name, Environment.GetEnvironmentVariable(name)));
				Environment.SetEnvironmentVariable(name, value);
			}

			try
			{
				_ = interpreter.RunValue(step.Task.Action);
				return null;
			}
			finally
			{
				for (var index = applied.Count - 1; index >= 0; index--)
					Environment.SetEnvironmentVariable(applied[index].Name, applied[index].Old);
			}
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

	private static int? RunCheck(BuildSession session, CheckStep step, TextWriter stderr)
	{
		List<BuildTargetResult> results;
		try
		{
			results = step.Target is { } target
				? [BuildTargetCompiler.Compile(session, target, output: null, write: false)]
				: [.. BuildTargetCompiler.BuildAllCheck(session)];
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}

		foreach (BuildTargetResult result in results)
			SourceCompiler.Report(result.Compile, stderr);
		return results.Any(result => !result.Compile.Success) ? 1 : null;
	}

	private static int? RunProgramStep(BuildSession session, RunStep step, TextWriter stderr)
	{
		TargetModel target = step.Target
			?? session.Workspace.DefaultAppTarget()
			?? throw new BuildException("в build.kern нет app-цели для :run");
		if (target.IsLibrary)
			throw new BuildException($"цель '{target.Name}' — библиотека; :run требует app-цель");

		BuildTargetResult built;
		try
		{
			built = BuildTargetCompiler.Compile(session, target, output: null, write: false);
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}

		SourceCompiler.Report(built.Compile, stderr);
		if (!built.Compile.Success)
			return 1;
		return RunCommand.RunCompiled(
			built.Compile.Program!,
			target.Args,
			session.EnvironmentFor(target),
			stderr,
			session.Root);
	}

	private static int? RunExe(BuildSession session, ExeStep step, TextWriter stdout, TextWriter stderr)
	{
		try
		{
			stdout.WriteLine(ExeCompiler.Build(session, step.Target));
			return null;
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}
	}

	private static int? RunDist(BuildSession session, DistStep step, TextWriter stdout, TextWriter stderr)
	{
		try
		{
			stdout.WriteLine(DistCompiler.Build(session, step.Target, new CliOptions(), stderr));
			return null;
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}
	}

	private static int? RunDll(BuildSession session, DllStep step, TextWriter stdout, TextWriter stderr)	{
		try
		{
			stdout.WriteLine(DllCompiler.Build(session, step.Target, output: null));
			return null;
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}
	}

	private static int? RunDis(BuildSession session, DisStep step, TextWriter stdout, TextWriter stderr)
	{
		IReadOnlyList<TargetModel> targets = step.Target is { } target
			? [target]
			: session.Workspace.Targets;
		foreach (TargetModel current in targets)
		{
			BuildTargetResult built;
			try
			{
				built = BuildTargetCompiler.Compile(session, current, output: null, write: false);
			}
			catch (BuildException exception)
			{
				stderr.WriteLine($"ошибка: {exception.Message}");
				return 1;
			}

			if (!built.Compile.Success)
			{
				SourceCompiler.Report(built.Compile, stderr);
				return 1;
			}

			stdout.WriteLine($"; цель {current.Name}");
			stdout.Write(Disassembler.Disassemble(built.Compile.Program!.Module));
		}

		return null;
	}
}
