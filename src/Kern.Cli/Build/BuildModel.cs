using Kern.Vm.Values;

namespace Kern.Cli.Build;

/// <summary>
/// Результат выполнения <c>build.kern</c> в обычных C#-структурах. Модель не зависит от VM,
/// поэтому команды и тесты работают с ней без интерпретатора; замыкания задач остаются
/// <see cref="KValue"/> и вызываются через <see cref="BuildScriptSession"/>.
/// </summary>
internal sealed class BuildModel
{
	public required string Root { get; init; }

	/// <summary>Имя проекта; пусто в <c>build.kern</c> — берётся имя папки.</summary>
	public required string Name { get; init; }

	public string Version { get; init; } = "0.0.0";

	public required CompilerSettingsModel Compiler { get; init; }

	public IReadOnlyList<string> Includes { get; init; } = [];

	public IReadOnlyList<string> LibPaths { get; init; } = [];

	public IReadOnlyList<ProfileModel> Profiles { get; init; } = [];

	public IReadOnlyList<TargetModel> Targets { get; init; } = [];

	public IReadOnlyList<TaskModel> Tasks { get; init; } = [];

	/// <summary>Была ли прочитана настоящая <c>build.kern</c> (иначе — соглашение §2).</summary>
	public bool HasBuildFile { get; init; }

	/// <summary>Цель по имени; регистр учитывается (имена — часть API сборки).</summary>
	public TargetModel? FindTarget(string name) =>
		Targets.FirstOrDefault(target => string.Equals(target.Name, name, StringComparison.Ordinal));

	public ProfileModel? FindProfile(string name) =>
		Profiles.LastOrDefault(profile => string.Equals(profile.Name, name, StringComparison.Ordinal));

	/// <summary>Цель по умолчанию: единственная app-цель, иначе цель с именем проекта.</summary>
	public TargetModel? DefaultAppTarget()
	{
		var apps = Targets.Where(target => target.Kind == "app").ToList();
		if (apps.Count == 1)
			return apps[0];
		return apps.FirstOrDefault(target => string.Equals(target.Name, Name, StringComparison.Ordinal)) ?? apps.FirstOrDefault();
	}
}

/// <summary><c>compiler_settings</c> из <c>kern.build</c> (§3, §7a).</summary>
internal sealed record CompilerSettingsModel
{
	public int Optimize { get; init; } = 1;

	public bool WarningsAsErrors { get; init; }

	public bool Macros { get; init; } = true;

	public bool DebugInfo { get; init; } = true;

	public static CompilerSettingsModel Default { get; } = new();

	/// <summary>Поле, отличное от значения по умолчанию, считается явной настройкой цели.</summary>
	public bool IsDefault =>
		Optimize == 1 && !WarningsAsErrors && Macros && DebugInfo;
}

/// <summary>Профиль сборки: <c>w.profile("release", …)</c> (§3, §7).</summary>
internal sealed record ProfileModel
{
	public required string Name { get; init; }

	public int Optimize { get; init; } = 1;

	public bool WarningsAsErrors { get; init; }

	public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
}

/// <summary>Цель сборки: приложение или библиотека (§3, §4).</summary>
internal sealed record TargetModel
{
	public required string Name { get; init; }

	public string Kind { get; init; } = "app";

	public IReadOnlyList<string> Src { get; init; } = ["src"];

	public string? Entry { get; init; } = "main";

	public IReadOnlyList<string> Deps { get; init; } = [];

	public IReadOnlyList<string> KbcPaths { get; init; } = [];

	public IReadOnlyList<string> Libs { get; init; } = [];

	public IReadOnlyList<string> LibPaths { get; init; } = [];

	public IReadOnlyList<string> Args { get; init; } = [];

	public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

	public CompilerSettingsModel Compiler { get; init; } = CompilerSettingsModel.Default;

	public IReadOnlyList<TaskModel> Tasks { get; init; } = [];

	/// <summary>
	/// Синтетическая цель проекта по соглашению: пути исходников считаются от корня проекта
	/// (как в <c>kernc build &lt;папка&gt;</c> до появления builder'а).
	/// </summary>
	public bool IsConvention { get; init; }

	public bool IsLibrary => Kind == "lib";

	public TaskModel? FindTask(string name) =>
		Tasks.FirstOrDefault(task => string.Equals(task.Name, name, StringComparison.Ordinal));
}

/// <summary>Пользовательская задача: действие-замыкание и список предварительных задач.</summary>
internal sealed record TaskModel
{
	public required string Name { get; init; }

	public IReadOnlyList<string> After { get; init; } = [];

	/// <summary>Замыкание Kern <c>fun() -> void</c>; исполняется тем же интерпретатором.</summary>
	public required KValue Action { get; init; }
}
