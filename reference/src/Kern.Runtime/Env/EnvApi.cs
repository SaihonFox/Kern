using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;

namespace Kern.Runtime.Env;

/// <summary>Окружение процесса: <c>env.get("HOME")</c>, <c>env.cwd</c>, <c>env.os</c>.</summary>
[KernType("env")]
public static class EnvApi
{
	[KernStatic] public static string? Get(string name) => Environment.GetEnvironmentVariable(name);
	[KernStatic] public static string GetOr(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;
	[KernStatic] public static void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

	[KernStatic, KernProperty(Setter = nameof(SetCwd))] public static string Cwd() => Environment.CurrentDirectory;
	public static void SetCwd(string path) => Environment.CurrentDirectory = path;
	[KernStatic, KernProperty] public static string Os() =>
		OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other";
	[KernStatic, KernProperty] public static string MachineName() => Environment.MachineName;
	[KernStatic, KernProperty] public static string UserName() => Environment.UserName;
	[KernStatic, KernProperty] public static int CpuCount() => Environment.ProcessorCount;
	[KernStatic, KernProperty] public static string NewLine() => Environment.NewLine;
}
