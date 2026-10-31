using System.Reflection;
using Kern.Binding.Catalog;
using Kern.Binding.Marshalling;
using Kern.Bytecode;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Project;
using Kern.Core;
using Kern.Runtime.Core;
using Kern.Vm.Execution;

namespace Kern.Tests.Harness;

/// <summary>Компиляция и запуск сниппетов Kern внутри тестов (тот же конвейер, что у kernc).</summary>
public static class KernHarness
{
	private static readonly Lazy<NativeCatalog> Catalog = new(() => new NativeCatalog([typeof(CoreApi).Assembly, .. LibraryLoader.Load([Path.Combine(RepoRoot(), "libs", "_out")])]));
	private static readonly object ConsoleLock = new();

	public static ProjectLoader CreateLoader() =>
		new(new CatalogNativeSource(Catalog.Value), new EmbeddedStdSource(Assembly.Load("Kern.Std")));

	/// <summary>Ошибки компиляции (пусто — программа корректна).</summary>
	public static List<string> Errors(ProjectModel project)
	{
		var errors = project.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.ToString()).ToList();
		if (errors.Count > 0) return errors;
		int seen = project.Diagnostics.Count;
		SemanticAnalyzer.Analyze(project);
		return [.. project.Diagnostics.Skip(seen).Where(d => d.Severity == Severity.Error).Select(d => d.ToString())];
	}

	public static KbcModule Compile(string source, int optimization = 1)
	{
		var project = CreateLoader().LoadText("test", "test.kern", source);
		var errors = project.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.ToString()).ToList();
		if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
		int seen = project.Diagnostics.Count;
		var program = SemanticAnalyzer.Analyze(project);
		errors = [.. project.Diagnostics.Skip(seen).Where(d => d.Severity == Severity.Error).Select(d => d.ToString())];
		if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
		return CodeGenerator.Generate(program, "test", optimization);
	}

	/// <summary>Выполнить main и вернуть всё, что программа напечатала в stdout.</summary>
	public static string Run(string source, int optimization = 1)
	{
		var module = Compile(source, optimization);
		lock (ConsoleLock)
		{
			var original = Console.Out;
			var writer = new StringWriter { NewLine = "\n" };
			Console.SetOut(writer);
			try
			{
				var errors = new StringWriter();
				int code = new Interpreter(module, new NativeResolver()).RunMain([], errors);
				if (code != 0) throw new InvalidOperationException($"код возврата {code}: {errors}");
				return writer.ToString();
			}
			finally { Console.SetOut(original); }
		}
	}

	/// <summary>Корень репозитория (папка с Kern.slnx).</summary>
	public static string RepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kern.slnx"))) dir = dir.Parent;
		return dir?.FullName ?? throw new InvalidOperationException("не найден Kern.slnx");
	}
}
