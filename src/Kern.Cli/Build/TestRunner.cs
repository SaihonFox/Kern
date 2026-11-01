using Kern.Binding.Resolution;
using Kern.Bytecode;
using Kern.Cli.Infrastructure;
using Kern.Vm.Execution;

namespace Kern.Cli.Build;

/// <summary>
/// Запуск <c>#[test]</c>-функций (встроенная задача <c>&lt;цель&gt;:test</c>).
/// Отдельного <c>kernc test</c> в stage 1 нет (spec §26), но builder может прогнать
/// помеченные функции: каждая вызывается без аргументов в своём интерпретаторе.
/// </summary>
internal static class TestRunner
{
	public static int Run(
		BuildSession session,
		TargetModel? target,
		TextWriter stdout,
		TextWriter stderr)
	{
		IReadOnlyList<TargetModel> targets = target is not null
			? [target]
			: session.Workspace.Targets;
		var failures = 0;
		var total = 0;
		foreach (TargetModel current in targets)
		{
			BuildTargetResult built = BuildTargetCompiler.Compile(session, current, output: null, write: false);
			if (!built.Compile.Success)
			{
				SourceCompiler.Report(built.Compile, stderr);
				return 1;
			}

			foreach (FunctionDef function in built.Compile.Program!.Module.FunctionPool)
			{
				if ((function.Flags & MemberFlags.Test) == 0)
					continue;
				total++;
				try
				{
					var interpreter = new Interpreter(built.Compile.Program.Module, new NativeResolver(built.Compile.Program.Catalog));
					_ = interpreter.Run(built.Compile.Program.Module.FunctionPool.IndexOf(function), []);
					stdout.WriteLine($"тест {function.Name}: ок");
				}
				catch (KernPanic panic)
				{
					failures++;
					stdout.WriteLine($"тест {function.Name}: ошибка: {panic.Message}");
				}
			}
		}

		if (total == 0)
		{
			stdout.WriteLine("тестов нет");
			return 0;
		}

		stdout.WriteLine($"тестов: {total}, успешно: {total - failures}, ошибок: {failures}");
		return failures == 0 ? 0 : 1;
	}
}
