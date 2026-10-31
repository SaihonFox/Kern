using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Vm.Values;

namespace Kern.Runtime.Build;

/// <summary>Задача, объявленная в build.kern: имя, зависимости (выполняются раньше) и действие на Kern.</summary>
public sealed record BuildTask(string Name, IReadOnlyList<string> After, KAction? Action);

/// <summary>
/// Корень сборки (`fun build(workspace w)` в build.kern): набор целей и своих задач.
/// <code>
/// use kern.build
/// fun build(workspace w) {
///     w.name = "game"
///     w.target("core") fun (target t) { t.kind = "lib" }
///     w.target("app")  fun (target t) { t.deps = ["core"]; t.entry = "main" }
/// }
/// </code>
/// </summary>
[KernType("workspace")]
public sealed class BuildWorkspace
{
	[KernProperty] public string Name { get; set; } = "workspace";
	[KernProperty] public string Version { get; set; } = "0.1.0";
	/// <summary>Задача по умолчанию для `kernc` без аргументов.</summary>
	[KernProperty] public string DefaultTask { get; set; } = "build";

	public List<BuildTarget> Targets { get; } = [];
	public List<BuildTask> Tasks { get; } = [];

	[KernMember]
	public void Target(string name, KAction<BuildTarget> configure)
	{
		if (Targets.Any(t => t.Name == name)) throw new InvalidOperationException($"цель '{name}' объявлена повторно");
		var target = new BuildTarget(name);
		Targets.Add(target);
		configure.Invoke(KValue.Obj(target));
	}

	/// <summary>Цель без настроек: каталог = имя, всё по умолчанию.</summary>
	[KernMember] public void Target(string name) => Targets.Add(new BuildTarget(name));

	[KernMember] public void Task(string name, KAction action) => Tasks.Add(new BuildTask(name, [], action));

	[KernMember] public void Task(string name, List<string> after, KAction action) => Tasks.Add(new BuildTask(name, after, action));
}

/// <summary>Цель сборки (аналог проекта Gradle): каталог, исходники, точка входа, зависимости от других целей.</summary>
[KernType("target")]
public sealed class BuildTarget(string name)
{
	[KernProperty] public string Name { get; } = name;
	/// <summary>Каталог цели относительно build.kern (по умолчанию — имя цели).</summary>
	[KernProperty] public string Path { get; set; } = name;
	/// <summary>Папка исходников внутри каталога цели.</summary>
	[KernProperty] public string Src { get; set; } = "src";
	/// <summary>Модуль с точкой входа (`main`); для библиотек не нужен.</summary>
	[KernProperty] public string? Entry { get; set; }
	/// <summary>"app" — исполняемая, "lib" — библиотека (подключается к другим целям исходниками).</summary>
	[KernProperty] public string Kind { get; set; } = "app";
	/// <summary>Имена целей, исходники которых компилируются вместе с этой.</summary>
	[KernProperty] public List<string> Deps { get; set; } = [];

	public List<BuildTask> Tasks { get; } = [];

	[KernMember] public void Task(string name, KAction action) => Tasks.Add(new BuildTask(name, [], action));

	[KernMember] public void Task(string name, List<string> after, KAction action) => Tasks.Add(new BuildTask(name, after, action));
}
