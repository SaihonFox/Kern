using System.Reflection;
using System.Text;
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

	public static bool Exists =>
		File.Exists(Path.Combine(AppContext.BaseDirectory, FileName)) || ResourceExists();

	private static bool ResourceExists() =>
		typeof(EmbeddedProgram).Assembly.GetManifestResourceInfo(FileName) is not null;

	public static int Run(IReadOnlyList<string> arguments, TextWriter stderr)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		ArgumentNullException.ThrowIfNull(stderr);

		string path = Path.Combine(AppContext.BaseDirectory, FileName);
		KbcModule module;
		try
		{
			byte[] bytes = File.Exists(path)
				? File.ReadAllBytes(path)
				: ReadResource() ?? throw new IOException($"не найден {FileName} ни рядом с хостом, ни во встроенных ресурсах");
			module = KbcReader.Read(bytes);
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

		var catalog = NativeCatalog.Scan(RuntimeAssemblies());
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

	private static byte[]? ReadResource()	{
		using Stream? stream = typeof(EmbeddedProgram).Assembly.GetManifestResourceStream(FileName);
		if (stream is null)
			return null;
		using MemoryStream buffer = new();
		stream.CopyTo(buffer);
		return buffer.ToArray();
	}

	/// <summary>
	/// Ядро + внешние <c>Kern.Lib.*.dll</c> из <c>libs/</c> рядом с хостом. Без этого
	/// программа, собранная <c>exe</c>/<c>dist</c>, не видела библиотеки (kern.db и др.).
	/// </summary>
	private static Assembly[] RuntimeAssemblies()
	{
		var assemblies = new List<Assembly> { typeof(CoreModule).Assembly };
		string libraries = Path.Combine(AppContext.BaseDirectory, "libs");
		if (!Directory.Exists(libraries))
			return [.. assemblies];
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string file in Directory.EnumerateFiles(libraries, "Kern.Lib.*.dll", SearchOption.AllDirectories))
		{
			if (!seen.Add(Path.GetFileName(file)))
				continue;
			try
			{
				assemblies.Add(Assembly.LoadFrom(file));
			}
			catch (Exception exception) when (exception is IOException or BadImageFormatException)
			{
			}
		}
		return [.. assemblies];
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
