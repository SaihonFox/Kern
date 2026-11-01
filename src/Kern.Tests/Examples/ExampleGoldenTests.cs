using Kern.Binding.Resolution;
using Kern.Bytecode;
using Kern.Cli.Build;
using Kern.Cli.Infrastructure;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Examples;

/// <summary>
/// M12: каждый пример с <c>expected.out</c> компилируется через тот же путь, что и
/// <c>kernc run</c> (build.kern или соглашение), выполняется и сравнивается с эталоном.
/// Переводы строк нормализуются, хвостовые пробелы/пустые строки игнорируются.
/// Набор пополняется по мере появления эталонов у остальных примеров.
/// </summary>
public sealed class ExampleGoldenTests
{
	[Theory]
	[MemberData(nameof(Goldens))]
	public void Example_matches_expected_output(string example)
	{
		string directory = Path.Combine(RepositoryRoot(), "examples", example);
		BuildSession session = BuildSession.Open(directory);
		BuildTargetResult built = BuildTargetCompiler.CompileApp(session);
		Assert.True(
			built.Compile.Success,
			$"пример {example} не компилируется:\n{string.Join('\n', built.Compile.ProjectMessages)}");

		string actual = Capture(built.Compile.Program!, out int exitCode);
		Assert.Equal(0, exitCode);

		string expected = File.ReadAllText(Path.Combine(directory, "expected.out"));
		Assert.Equal(Normalize(expected), Normalize(actual));
	}

	public static IEnumerable<object[]> Goldens()
	{
		string root = Path.Combine(RepositoryRoot(), "examples");
		foreach (string directory in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.Ordinal))
		{
			// `.golden-pending` — эталон ждёт решения владельца (Q25: кавычки вложенных str).
			if (!File.Exists(Path.Combine(directory, "expected.out")) ||
				File.Exists(Path.Combine(directory, ".golden-pending")))
				continue;
			yield return [Path.GetFileName(directory)];
		}
	}

	private static string Capture(CompiledProgram program, out int exitCode)
	{
		var writer = new StringWriter { NewLine = "\n" };
		TextWriter original = Console.Out;
		Console.SetOut(writer);
		try
		{
			var interpreter = new Interpreter(program.Module, new NativeResolver(program.Catalog));
			int entry = program.Module.EntryPoint;
			Assert.True(entry >= 0, "в примере нет fun main");
			KValue[] arguments = program.Module.FunctionPool[entry].ParamCount == 1
				? [KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Str), []))]
				: [];
			KValue result = Pump(interpreter, interpreter.Run(entry, arguments));
			exitCode = result.Kind == ValueKind.I32 ? result.AsI32 : 0;
		}
		finally
		{
			Console.SetOut(original);
		}

		return writer.ToString();
	}

	private static KValue Pump(Interpreter interpreter, KValue value)
	{
		if (value.Kind != ValueKind.Ref || value.Ref is not KTask task)
			return value;

		long start = Environment.TickCount64;
		while (!task.IsCompleted)
		{
			if (interpreter.Scheduler.HasWork)
			{
				interpreter.Scheduler.RunUntilIdle();
				continue;
			}

			Assert.True(Environment.TickCount64 - start < 30_000, "асинхронный main не завершился");
			interpreter.Scheduler.WaitForWork(TimeSpan.FromMilliseconds(50));
		}

		if (task.Status == KTaskStatus.Failed && task.Error is { } error)
			throw error;
		return task.Result;
	}

	private static string Normalize(string text) =>
		text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();

	private static string RepositoryRoot()
	{
		string? current = AppContext.BaseDirectory;
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current, "Kern.slnx")))
				return current;
			current = Path.GetDirectoryName(current);
		}

		throw new InvalidOperationException("не найден корень репозитория (Kern.slnx)");
	}
}
