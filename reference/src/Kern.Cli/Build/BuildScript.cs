using Kern.Bytecode;
using Kern.Cli.Infrastructure;
using Kern.Runtime.Build;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Cli.Build;

/// <summary>Загруженный build.kern: рабочий VM (в нём живут лямбды задач) и заполненная модель сборки.</summary>
internal sealed class BuildScript
{
	public const string FileName = "build.kern";

	public required string Root { get; init; }
	public required BuildWorkspace Workspace { get; init; }
	public required Interpreter Vm { get; init; }

	public static bool Exists(string dir) => File.Exists(Path.Combine(dir, FileName));

	/// <summary>Компилирует и выполняет `fun build(workspace w)`; null — ошибки (уже напечатаны).</summary>
	public static BuildScript? Load(string root)
	{
		string file = Path.Combine(root, FileName);
		var module = Pipeline.Compile(file, optimization: 0);
		if (module is null) return null;

		int index = module.Functions.FindIndex(f => f.Name == "build" && f.Owner < 0);
		if (index < 0)
		{
			Console.Error.WriteLine($"error: в {FileName} нет функции `fun build(workspace w)`");
			return null;
		}

		var vm = Pipeline.CreateVm(module);
		var workspace = new BuildWorkspace { Name = new DirectoryInfo(Path.GetFullPath(root)).Name };
		try
		{
			vm.Initialize();
			vm.CallFunctionIndex(index, KValue.Obj(workspace));
		}
		catch (KernPanic p)
		{
			Console.Error.WriteLine(p.Format());
			return null;
		}
		return new BuildScript { Root = Path.GetFullPath(root), Workspace = workspace, Vm = vm };
	}
}
