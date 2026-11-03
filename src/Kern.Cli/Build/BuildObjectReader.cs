using Kern.Vm.Values;

namespace Kern.Cli.Build;

/// <summary>
/// Чтение полей объектов <c>workspace</c>/<c>target</c>/<c>profile</c> из VM по именам.
/// Слоты берутся из <c>TypeDef.Fields</c>, поэтому перестановка полей в
/// <see cref="BuildPrelude"/> ничего не ломает.
/// </summary>
internal static class BuildObjectReader
{
	public static BuildModel ReadWorkspace(KValue value, string root, string? scriptPath, bool hasBuildFile)
	{
		KObject workspace = Object(value, "workspace");
		string name = ReadString(workspace, "name");
		if (string.IsNullOrWhiteSpace(name))
			name = new DirectoryInfo(root).Name;

		return new BuildModel
		{
			Root = root,
			Name = name,
			Version = ReadString(workspace, "version"),
			Compiler = ReadSettings(Field(workspace, "compiler")),
			Includes = ReadStrings(workspace, "includes"),
			LibPaths = ReadStrings(workspace, "lib_paths"),
			Profiles = [.. ReadObjects(workspace, "profiles").Select(ReadProfile)],
			Targets = [.. ReadObjects(workspace, "targets").Select(ReadTarget)],
			Tasks = [.. ReadObjects(workspace, "tasks").Select(ReadTask)],
			HasBuildFile = hasBuildFile,
		};
	}

	public static ProfileModel ReadProfile(KValue value)
	{
		KObject profile = Object(value, "profile");
		return new ProfileModel
		{
			Name = ReadString(profile, "name"),
			Optimize = ReadInt(profile, "optimize"),
			WarningsAsErrors = ReadBool(profile, "warnings_as_errors"),
			Env = ReadStringMap(profile, "env"),
		};
	}

	public static TargetModel ReadTarget(KValue value)
	{
		KObject target = Object(value, "target");
		return new TargetModel
		{
			Name = ReadString(target, "name"),
			Kind = ReadString(target, "kind"),
			Src = ReadStrings(target, "src"),
			Entry = ReadString(target, "entry"),
			Deps = ReadStrings(target, "deps"),
			KbcPaths = ReadStrings(target, "kbc_paths"),
			Libs = ReadStrings(target, "libs"),
			LibPaths = ReadStrings(target, "lib_paths"),
			Packages = [.. ReadObjects(target, "packages").Select(ReadPackage)],
			Args = ReadStrings(target, "args"),
			Env = ReadStringMap(target, "env"),
			Compiler = ReadSettings(Field(target, "compiler")),
			Tasks = [.. ReadObjects(target, "tasks").Select(ReadTask)],
		};
	}

	public static TaskModel ReadTask(KValue value)
	{
		KObject task = Object(value, "build_task");
		return new TaskModel
		{
			Name = ReadString(task, "name"),
			After = ReadStrings(task, "after"),
			Action = Field(task, "action"),
		};
	}

	public static PackageModel ReadPackage(KValue value)
	{
		KObject package = Object(value, "package");
		return new PackageModel
		{
			Name = ReadString(package, "name"),
			Url = ReadString(package, "url"),
			Sha256 = ReadString(package, "sha256"),
		};
	}

	public static CompilerSettingsModel ReadSettings(KValue value)
	{
		KObject settings = Object(value, "compiler_settings");
		return new CompilerSettingsModel
		{
			Optimize = ReadInt(settings, "optimize"),
			WarningsAsErrors = ReadBool(settings, "warnings_as_errors"),
			Macros = ReadBool(settings, "macros"),
			DebugInfo = ReadBool(settings, "debug_info"),
		};
	}

	private static KObject Object(KValue value, string type) =>
		value.Ref as KObject ?? throw new BuildException($"build.kern: ожидался объект '{type}', получено {value.TypeName}");

	private static KValue Field(KObject instance, string name)
	{
		foreach (var field in instance.Type.Definition.Fields)
			if (string.Equals(field.Name, name, StringComparison.Ordinal))
				return instance.GetField(field.Slot);

		throw new BuildException($"build.kern: у типа '{instance.Type.Canonical}' нет поля '{name}'");
	}

	private static string ReadString(KObject instance, string name)
	{
		KValue value = Field(instance, name);
		return value.Kind == ValueKind.Str ? value.AsStr : string.Empty;
	}

	private static int ReadInt(KObject instance, string name)
	{
		KValue value = Field(instance, name);
		return value.Kind == ValueKind.I32 ? value.AsI32 : 0;
	}

	private static bool ReadBool(KObject instance, string name)
	{
		KValue value = Field(instance, name);
		return value.Kind == ValueKind.Bool && value.AsBool;
	}

	private static IReadOnlyList<string> ReadStrings(KObject instance, string name)
	{
		KValue value = Field(instance, name);
		if (value.Ref is not KList list)
			return [];

		var result = new List<string>(list.Count);
		foreach (KValue item in list.Items)
			if (item.Kind == ValueKind.Str)
				result.Add(item.AsStr);
		return result;
	}

	private static IReadOnlyList<KValue> ReadObjects(KObject instance, string name) =>
		Field(instance, name).Ref is KList list ? [.. list.Items] : [];

	private static IReadOnlyDictionary<string, string> ReadStringMap(KObject instance, string name)
	{
		KValue value = Field(instance, name);
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		if (value.Ref is not KDict dict)
			return result;

		foreach (var (key, item) in dict.Items)
			if (key.Kind == ValueKind.Str && item.Kind == ValueKind.Str)
				result[key.AsStr] = item.AsStr;
		return result;
	}
}
