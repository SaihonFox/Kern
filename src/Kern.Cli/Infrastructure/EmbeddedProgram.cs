using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Bytecode;
using Kern.Runtime.Core;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Cli.Infrastructure;

/// <summary>
/// Запуск <c>program.kbc</c>, лежащего рядом с хостом <c>kernc</c>: так работает
/// <c>kernc exe</c> (docs/BUILDER.md §2 — хост + байткод).
/// </summary>
internal static class EmbeddedProgram
{
	public const string FileName = "program.kbc";

	public static bool Exists => File.Exists(Path.Combine(AppContext.BaseDirectory, FileName));

	public static int Run(IReadOnlyList<string> arguments, TextWriter stderr)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		ArgumentNullException.ThrowIfNull(stderr);

		string path = Path.Combine(AppContext.BaseDirectory, FileName);
		KbcModule module;
		try
		{
			module = KbcReader.Read(File.ReadAllBytes(path));
			KbcModuleValidator.Validate(module);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			stderr.WriteLine($"ошибка: не удалось прочитать {path}: {exception.Message}");
			return 1;
		}
		catch (KbcFormatException exception)
		{
			stderr.WriteLine($"ошибка: {path}: {exception.Message}");
			return 1;
		}

		if (module.EntryPoint < 0)
		{
			stderr.WriteLine($"ошибка: в {path} нет точки входа");
			return 1;
		}

		var catalog = NativeCatalog.Scan(typeof(CoreModule).Assembly);
		FunctionDef entry = module.FunctionPool[module.EntryPoint];
		try
		{
			var interpreter = new Interpreter(module, new NativeResolver(catalog));
			KValue[] values = entry.ParamCount == 1
				? [KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Str), arguments.Select(KValue.Str)))]
				: [];
			KValue result = Wait(interpreter, interpreter.Run(module.EntryPoint, values));
			return result.Kind == ValueKind.I32 ? result.AsI32 : 0;
		}
		catch (KernPanic panic)
		{
			stderr.WriteLine($"ошибка выполнения: {panic.Message}");
			return 1;
		}
	}

	private static KValue Wait(Interpreter interpreter, KValue value)
	{
		if (value.Ref is not KTask task)
			return value;

		long start = Environment.TickCount64;
		while (!task.IsCompleted)
		{
			if (interpreter.Scheduler.HasWork)
			{
				interpreter.Scheduler.RunUntilIdle();
				continue;
			}

			if (Environment.TickCount64 - start > 30_000)
				throw new KernPanic("асинхронная операция не завершилась: нет внешних событий");
			interpreter.Scheduler.WaitForWork(TimeSpan.FromMilliseconds(50));
		}

		return task.Unwrap();
	}
}
