using Kern.Binding.Resolution;
using Kern.Bytecode;
using Kern.Cli.Build;
using Kern.Cli.Infrastructure;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Cli.Commands;

/// <summary>
/// <c>kernc run &lt;файл.kern | папка&gt; [аргумент ...]</c>: компилирует один файл или проект
/// по соглашению и выполняет его top-level <c>fun main</c>. Аргументы после исходника
/// передаются в <c>main(list&lt;str&gt; args)</c> (spec §13); <c>main</c> без параметров их
/// игнорирует. Асинхронный <c>main</c> (возвращает <c>task&lt;T&gt;</c>) доводится до конца
/// планировщиком VM. Вывод программы идёт через <see cref="Console"/>.
/// </summary>
internal static class RunCommand
{
	private const int AsyncWaitTimeoutMs = 30_000;

	public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
	{
		if (!SourceArguments.TryParse(
			args, "run", allowOutput: false, allowProgramArguments: true, stderr,
			out string path, out SourceKind kind, out _, out IReadOnlyList<string> programArguments,
			out CliOptions options))
			return 2;

		if (kind == SourceKind.Project && File.Exists(Path.Combine(path, BuildSession.ScriptName)))
			return RunProject(path, programArguments, options, stderr);

		CompileResult result = SourceCompiler.Compile(path, kind, options.Apply(CompileOptions.Default));
		SourceCompiler.Report(result, stderr);
		if (!result.Success)
			return 1;

		return Execute(result.Program!, kind, programArguments, stderr);
	}

	/// <summary><c>run</c> проекта с <c>build.kern</c>: компилирует app-цель без записи .kbc.</summary>
	private static int RunProject(
		string path,
		IReadOnlyList<string> programArguments,
		CliOptions options,
		TextWriter stderr)
	{
		BuildTargetResult built;
		IReadOnlyDictionary<string, string> environment;
		try
		{
			BuildSession session = BuildSession.Open(path);
			session.OptimizeOverride = options.Optimize;
			session.ProfileName = options.Profile;
			built = BuildTargetCompiler.CompileApp(session);
			environment = session.EnvironmentFor(built.Target);
		}
		catch (BuildException exception)
		{
			stderr.WriteLine($"ошибка: {exception.Message}");
			return 1;
		}

		SourceCompiler.Report(built.Compile, stderr);
		if (!built.Compile.Success)
			return 1;

		IReadOnlyList<string> arguments = programArguments.Count > 0 ? programArguments : built.Target.Args;
		return Execute(built.Compile.Program!, SourceKind.Project, arguments, stderr, environment);
	}

	/// <summary>Выполнить уже скомпилированный проект (встроенная задача <c>:run</c>).</summary>
	internal static int RunCompiled(
		CompiledProgram program,
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string>? environment,
		TextWriter stderr) =>
		Execute(program, SourceKind.Project, arguments, stderr, environment);

	private static int Execute(
		CompiledProgram program,
		SourceKind kind,
		IReadOnlyList<string> programArguments,
		TextWriter stderr,
		IReadOnlyDictionary<string, string>? environment = null)
	{
		int entry = program.Module.EntryPoint;
		if (entry < 0)
		{
			stderr.WriteLine(kind == SourceKind.Project
				? "ошибка: в проекте нет функции main"
				: "ошибка: в файле нет функции main");
			return 1;
		}

		FunctionDef entryPoint = program.Module.FunctionPool[entry];
		var applied = new List<(string Name, string? Old)>();
		try
		{
			if (environment is not null)
				foreach (var (name, value) in environment)
				{
					applied.Add((name, Environment.GetEnvironmentVariable(name)));
					Environment.SetEnvironmentVariable(name, value);
				}

			var interpreter = new Interpreter(program.Module, new NativeResolver(program.Catalog));
			KValue[] mainArguments = entryPoint.ParamCount == 1
				? [KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Str), programArguments.Select(KValue.Str)))]
				: [];
			KValue mainResult = WaitForTask(interpreter, interpreter.Run(entry, mainArguments));
			// `main() -> i32` задаёт код выхода; void-main и прочие значения — 0.
			return mainResult.Kind == ValueKind.I32 ? mainResult.AsI32 : 0;
		}
		catch (KernPanic panic)
		{
			stderr.WriteLine($"ошибка выполнения: {panic.Message}");
			return 1;
		}
		finally
		{
			for (var index = applied.Count - 1; index >= 0; index--)
				Environment.SetEnvironmentVariable(applied[index].Name, applied[index].Old);
		}
	}

	/// <summary>
	/// Если <c>main</c> асинхронный, <see cref="Interpreter.Run"/> вернул <c>task&lt;T&gt;</c>:
	/// крутим планировщик в текущем потоке, как это делает сам интерпретатор на <c>await</c>.
	/// </summary>
	private static KValue WaitForTask(Interpreter interpreter, KValue value)
	{
		if (value.Kind != ValueKind.Ref || value.Ref is not KTask task)
			return value;

		long startTicks = Environment.TickCount64;
		while (!task.IsCompleted)
		{
			if (interpreter.Scheduler.HasWork)
			{
				interpreter.Scheduler.RunUntilIdle();
				continue;
			}

			if (Environment.TickCount64 - startTicks > AsyncWaitTimeoutMs)
				throw new KernPanic("асинхронная операция не завершилась: нет внешних событий");

			interpreter.Scheduler.WaitForWork(TimeSpan.FromMilliseconds(50));
		}

		if (task.Status == KTaskStatus.Failed && task.Error is { } error)
			throw error;
		if (task.Status == KTaskStatus.Canceled)
			throw new KernPanic("задача main отменена");

		return task.Result;
	}
}
