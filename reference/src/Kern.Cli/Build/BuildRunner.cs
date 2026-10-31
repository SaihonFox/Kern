using System.Security.Cryptography;
using System.Text;
using Kern.Bytecode;
using Kern.Bytecode.Serialization;
using Kern.Cli.Infrastructure;
using Kern.Runtime.Build;
using Kern.Vm.Execution;

namespace Kern.Cli.Build;

/// <summary>
/// Граф задач в духе Gradle: встроенные (`build`, `test`, `clean`, `цель:compile|run|test|exe|clean`)
/// и объявленные в build.kern (`w.task(...)`, `t.task(...)`). Задача выполняется один раз, после своих зависимостей.
/// Компиляция цели пропускается, если хэш исходников не изменился.
/// </summary>
internal sealed class BuildRunner
{
	private sealed record Node(string Name, string Description, IReadOnlyList<string> After, Func<int> Run);

	private readonly BuildScript _script;
	private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
	private readonly HashSet<string> _done = [];
	private readonly HashSet<string> _active = [];
	private readonly int _optimization;
	private string[] _programArgs = [];

	public BuildRunner(BuildScript script, int optimization)
	{
		_script = script;
		_optimization = optimization;
		Register();
	}

	private BuildWorkspace Workspace => _script.Workspace;
	private string BuildDir => Path.Combine(_script.Root, "build");

	public IEnumerable<(string Name, string Description)> Tasks => _nodes.Values.OrderBy(n => n.Name, StringComparer.Ordinal).Select(n => (n.Name, n.Description));

	public IEnumerable<string> AppTargets => Workspace.Targets.Where(t => t.Kind == "app").Select(t => t.Name);

	public string DefaultTask => Workspace.DefaultTask;

	public bool Has(string task) => _nodes.ContainsKey(task);

	// ------------------------------------------------------------ регистрация

	private void Add(string name, string description, IEnumerable<string> after, Func<int> run) =>
		_nodes[name] = new Node(name, description, [.. after], run);

	private void Register()
	{
		foreach (var t in Workspace.Targets)
		{
			var target = t;
			string compile = target.Name + ":compile";
			Add(compile, $"скомпилировать цель '{target.Name}'", [], () => Compile(target));
			Add(target.Name + ":run", $"запустить '{target.Name}'", [compile], () => Run(target));
			Add(target.Name + ":test", $"тесты '{target.Name}'", [compile], () => Test(target));
			Add(target.Name + ":exe", $"автономное приложение '{target.Name}'", [compile], () => Exe(target));
			Add(target.Name + ":clean", $"очистить '{target.Name}'", [], () => Clean(target));
			foreach (var task in target.Tasks) AddCustom(target.Name + ":" + task.Name, task, target.Name);
		}
		Add("build", "скомпилировать все цели", Workspace.Targets.Select(t => t.Name + ":compile"), () => 0);
		Add("test", "тесты всех целей", Workspace.Targets.Select(t => t.Name + ":test"), () => 0);
		Add("clean", "удалить каталог build", [], () =>
		{
			if (Directory.Exists(BuildDir)) Directory.Delete(BuildDir, recursive: true);
			Console.Out.WriteLine("build очищен");
			return 0;
		});
		foreach (var task in Workspace.Tasks) AddCustom(task.Name, task, null);
	}

	private void AddCustom(string fullName, BuildTask task, string? targetScope)
	{
		// зависимость `name` внутри цели означает `цель:name`, если такая задача есть
		var after = task.After.Select(a => targetScope is not null && !a.Contains(':') ? targetScope + ":" + a : a);
		Add(fullName, "задача из build.kern", after, () =>
		{
			try { task.Action?.Invoke(); }
			catch (KernPanic p) { Console.Error.WriteLine(p.Format()); return 1; }
			return 0;
		});
	}

	// ------------------------------------------------------------ выполнение

	/// <summary>Выполняет задачи по порядку со всеми зависимостями; возвращает код завершения.</summary>
	public int Execute(IEnumerable<string> tasks, string[] programArgs)
	{
		_programArgs = programArgs;
		foreach (var name in tasks)
		{
			int code = ExecuteOne(name);
			if (code != 0) return code;
		}
		return 0;
	}

	private int ExecuteOne(string name)
	{
		if (_done.Contains(name)) return 0;
		if (!_nodes.TryGetValue(name, out var node))
		{
			Console.Error.WriteLine($"error: задача '{name}' не найдена (список: kernc tasks)");
			return 2;
		}
		if (!_active.Add(name))
		{
			Console.Error.WriteLine($"error: циклическая зависимость задач через '{name}'");
			return 2;
		}
		foreach (var dep in node.After)
		{
			int c = ExecuteOne(dep);
			if (c != 0) { _active.Remove(name); return c; }
		}
		int code = node.Run();
		_active.Remove(name);
		if (code == 0) _done.Add(name);
		return code;
	}

	// ------------------------------------------------------------ встроенные задачи целей

	private BuildTarget Find(string name) => Workspace.Targets.First(t => t.Name == name);

	private string TargetRoot(BuildTarget t) => Path.GetFullPath(Path.Combine(_script.Root, t.Path));

	/// <summary>Каталоги исходников цели и (транзитивно) её зависимостей; собственный — первым.</summary>
	private List<string> SourceDirs(BuildTarget target)
	{
		var result = new List<string>();
		var seen = new HashSet<string>();
		void Visit(BuildTarget t)
		{
			if (!seen.Add(t.Name)) return;
			string dir = Path.Combine(TargetRoot(t), t.Src);
			result.Add(Directory.Exists(dir) ? dir : TargetRoot(t));
			foreach (var d in t.Deps)
			{
				var dep = Workspace.Targets.FirstOrDefault(x => x.Name == d)
					?? throw new InvalidOperationException($"цель '{t.Name}' зависит от неизвестной цели '{d}'");
				Visit(dep);
			}
		}
		Visit(target);
		return result;
	}

	private string KbcPath(BuildTarget t) => Path.Combine(BuildDir, t.Name + ".kbc");

	private static string HashSources(IEnumerable<string> dirs, int optimization)
	{
		using var sha = SHA256.Create();
		var sb = new StringBuilder().Append("O").Append(optimization).Append('\n');
		foreach (var dir in dirs)
			foreach (var f in Directory.EnumerateFiles(dir, "*.kern", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
				sb.Append(Path.GetRelativePath(dir, f)).Append(':').Append(Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(f)))).Append('\n');
		return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
	}

	private int Compile(BuildTarget target)
	{
		var dirs = SourceDirs(target);
		string kbc = KbcPath(target), stamp = kbc + ".hash";
		string hash = HashSources(dirs, _optimization);
		if (File.Exists(kbc) && File.Exists(stamp) && File.ReadAllText(stamp) == hash)
		{
			Console.Out.WriteLine($"> {target.Name}:compile  up-to-date");
			return 0;
		}
		Console.Out.WriteLine($"> {target.Name}:compile");
		var module = Pipeline.CompileSources(target.Name, TargetRoot(target), dirs, target.Entry, _optimization);
		if (module is null) return 1;
		if (target.Kind == "app" && module.EntryPoint < 0)
		{
			Console.Error.WriteLine($"error: у цели '{target.Name}' нет точки входа `main` (kind = \"lib\" для библиотек)");
			return 1;
		}
		Directory.CreateDirectory(BuildDir);
		File.WriteAllBytes(kbc, KbcWriter.Write(module));
		File.WriteAllText(stamp, hash);
		return 0;
	}

	private KbcModule Load(BuildTarget target) => KbcReader.Read(File.ReadAllBytes(KbcPath(target)));

	private int Run(BuildTarget target)
	{
		if (target.Kind != "app")
		{
			Console.Error.WriteLine($"error: цель '{target.Name}' — библиотека, её нельзя запустить");
			return 2;
		}
		Console.Out.WriteLine($"> {target.Name}:run");
		return Pipeline.CreateVm(Load(target)).RunMain(_programArgs);
	}

	private int Test(BuildTarget target)
	{
		Console.Out.WriteLine($"> {target.Name}:test");
		return Commands.CliCommands.RunTests(Load(target));
	}

	private int Exe(BuildTarget target)
	{
		string dir = Path.Combine(BuildDir, "dist", target.Name);
		Console.Out.WriteLine($"> {target.Name}:exe → {dir}");
		return Commands.CliCommands.WriteExe(Load(target), dir);
	}

	private int Clean(BuildTarget target)
	{
		foreach (var f in new[] { KbcPath(target), KbcPath(target) + ".hash" })
			if (File.Exists(f)) File.Delete(f);
		return 0;
	}
}
