using Kern.Bytecode;
using Kern.Bytecode.Serialization;
using Kern.Cli.Infrastructure;
using Kern.Vm.Execution;

namespace Kern.Cli.Commands;

internal static class CliCommands
{
	public static int Check(string path) => Pipeline.Analyze(path, out _) is null ? 1 : Ok($"{path}: ошибок нет");

	/// <summary>
	/// Куда писать .kbc по умолчанию: `&lt;проект&gt;/out/&lt;имя&gt;.kbc` (рядом с исходниками, а не в текущую папку kernc — там обычно bin C#-проекта).
	/// Явный `-o` берётся как есть.
	/// </summary>
	private static string ResolveOutput(string path, string? output, string name)
	{
		if (output is not null) return output;
		string dir = Path.Combine(Pipeline.ProjectRoot(path), "out");
		Directory.CreateDirectory(dir);
		return Path.Combine(dir, name + ".kbc");
	}

	public static int Build(string path, string? output, int opt)
	{
		var module = Pipeline.Compile(path, opt);
		if (module is null) return 1;
		output = ResolveOutput(path, output, module.Name);
		File.WriteAllBytes(output, KbcWriter.Write(module));
		return Ok($"{output}");
	}

	/// <summary>Библиотека: .kbc без точки входа (`main` отбрасывается). Подключается как зависимость цели в build.kern.</summary>
	public static int Dll(string path, string? output, int opt)
	{
		var project = Pipeline.Load(path, out var name);
		if (project is null) return 1;
		var program = Pipeline.Analyze(project);
		if (program is null) return 1;
		var module = Kern.Compiler.CodeGen.CodeGenerator.Generate(program, name, opt);
		Pipeline.MakePathsRelative(module, Pipeline.ProjectRoot(path));
		module = Pipeline.Link(module, Pipeline.ProjectRoot(path));
		if (module is null) return 1;
		// интерфейсные исходники (тела вырезаны): по ним потребитель проверяет типы и вызовы, код подставляет линкер
		module.Attributes.AddRange(Kbc.KbcStubs.Build(project));
		bool hadEntry = module.EntryPoint >= 0;
		module.EntryPoint = -1;
		module.EntryTakesArgs = false;
		module.EntryIsAsync = false;
		output = ResolveOutput(path, output, module.Name);
		File.WriteAllBytes(output, KbcWriter.Write(module));
		return Ok(hadEntry ? $"{output} (точка входа `main` убрана: это библиотека)" : output);
	}

	public static int Run(string path, string[] args, int opt, bool showTime = false)
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var module = path.EndsWith(".kbc", StringComparison.OrdinalIgnoreCase)
			? KbcReader.Read(File.ReadAllBytes(path))
			: Pipeline.Compile(path, opt);
		if (module is null) return 1;
		var compiled = sw.Elapsed;
		sw.Restart();
		int code = Pipeline.CreateVm(module).RunMain(args);
		if (showTime) Console.Error.WriteLine($"[время] загрузка/компиляция: {FormatElapsed(compiled)}, выполнение: {FormatElapsed(sw.Elapsed)}");
		return code;
	}

	/// <summary>`850 мс`, `2.34 с`, `1 мин 05 с`.</summary>
	public static string FormatElapsed(TimeSpan t) =>
		t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:0.#} мс"
		: t.TotalMinutes < 1 ? $"{t.TotalSeconds:0.00} с"
		: $"{(int)t.TotalMinutes} мин {t.Seconds:00} с";

	public static int Disassemble(string path, int opt)
	{
		var module = path.EndsWith(".kbc", StringComparison.OrdinalIgnoreCase)
			? KbcReader.Read(File.ReadAllBytes(path))
			: Pipeline.Compile(path, opt);
		if (module is null) return 1;
		Console.Out.Write(Disassembler.Disassemble(module));
		return 0;
	}

	/// <summary>Автономный запуск: копия хоста kernc + program.kbc рядом.</summary>
	public static int Exe(string path, string? outputDir, int opt)
	{
		var module = Pipeline.Compile(path, opt);
		if (module is null) return 1;
		return WriteExe(module, outputDir ?? Path.Combine(Pipeline.ProjectRoot(path), "dist", module.Name));
	}

	public static int WriteExe(KbcModule module, string outputDir)
	{
		Directory.CreateDirectory(outputDir);
		string host = AppContext.BaseDirectory;
		foreach (var f in Directory.EnumerateFiles(host))
		{
			var name = Path.GetFileName(f);
			if (name.StartsWith("kernc", StringComparison.OrdinalIgnoreCase))
				name = module.Name + name["kernc".Length..];
			File.Copy(f, Path.Combine(outputDir, name), overwrite: true);
		}
		File.WriteAllBytes(Path.Combine(outputDir, Program.EmbeddedProgram), KbcWriter.Write(module));
		return Ok($"{outputDir}");
	}

	public static int Test(string path, int opt)
	{
		var module = Pipeline.Compile(path, opt);
		return module is null ? 1 : RunTests(module);
	}

	public static int RunTests(KbcModule module)
	{
		var vm = Pipeline.CreateVm(module);
		vm.Initialize();
		int passed = 0, failed = 0;
		foreach (int index in module.Tests)
		{
			var fd = module.Functions[index];
			string name = fd.Owner >= 0 ? $"{module.Types[fd.Owner].FullName}.{fd.Name}" : fd.Name;
			try
			{
				var r = vm.CallFunctionIndex(index);
				if (r.Ref is Kern.Vm.Async.KTask t) vm.Scheduler.RunUntil(() => t.IsCompleted);
				Console.Out.WriteLine($"  ok    {name}");
				passed++;
			}
			catch (KernPanic p)
			{
				Console.Out.WriteLine($"  FAIL  {name}: {p.Message}");
				failed++;
			}
		}
		Console.Out.WriteLine($"тестов: {passed + failed}, успешно: {passed}, провалено: {failed}");
		return failed == 0 ? 0 : 1;
	}

	private static int Ok(string message)
	{
		Console.Out.WriteLine(message);
		return 0;
	}
}
