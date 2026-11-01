using Kern.Binding.Resolution;
using Kern.Bytecode;
using Kern.Cli.Infrastructure;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Cli.Build;

/// <summary>
/// Запуск <c>#[test]</c>-функций: встроенная задача <c>&lt;цель&gt;:test</c> и команда
/// <c>kernc test</c>. Каждая помеченная функция вызывается без аргументов в своём
/// интерпретаторе; паника теста считается ошибкой.
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

			var (targetTotal, targetFailures) = RunTests(built.Compile.Program!, stdout);
			total += targetTotal;
			failures += targetFailures;
		}

		return Report(total, failures, stdout);
	}

	/// <summary>Прогнать тесты уже скомпилированного модуля (одиночный <c>.kern</c>).</summary>
	public static int Run(CompiledProgram program, TextWriter stdout)
	{
		ArgumentNullException.ThrowIfNull(program);
		ArgumentNullException.ThrowIfNull(stdout);

		var (total, failures) = RunTests(program, stdout);
		return Report(total, failures, stdout);
	}

	private static (int Total, int Failures) RunTests(CompiledProgram program, TextWriter stdout)
	{
		var failures = 0;
		var total = 0;
		KbcModule module = program.Module;
		for (var index = 0; index < module.FunctionPool.Count; index++)
		{
			FunctionDef function = module.FunctionPool[index];
			if ((function.Flags & MemberFlags.Test) == 0)
				continue;
			total++;
			try
			{
				var interpreter = new Interpreter(module, new NativeResolver(program.Catalog));
				_ = interpreter.Run(index, []);
				stdout.WriteLine($"тест {function.Name}: ок");
			}
			catch (KernPanic panic)
			{
				failures++;
				stdout.WriteLine($"тест {function.Name}: ошибка: {panic.Message}");
			}
		}

		return (total, failures);
	}

	private static int Report(int total, int failures, TextWriter stdout)
	{
		if (total == 0)
		{
			stdout.WriteLine("тестов нет");
			return 0;
		}

		stdout.WriteLine($"тестов: {total}, успешно: {total - failures}, ошибок: {failures}");
		return failures == 0 ? 0 : 1;
	}
}
