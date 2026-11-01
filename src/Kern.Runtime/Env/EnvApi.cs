using Kern.Binding.Attributes;

namespace Kern.Runtime.Env;

/// <summary>
/// Модуль <c>kern.env</c> (spec §16, раунд 4): переменные окружения, имя ОС и текущий
/// каталог. <c>env.os</c>/<c>env.cwd</c> — свойства (короткие имена, spec §16), а не
/// функции; <c>env.get</c> возвращает <c>str?</c>, чтобы отсутствие переменной было
/// видно явно.
/// </summary>
[KernModule("kern.env")]
public static class EnvApi
{
	/// <summary><c>env.get(name) -> str?</c>: значение переменной или <c>nil</c>.</summary>
	[KernMember]
	public static string? Get(string name) => Environment.GetEnvironmentVariable(name);

	/// <summary><c>env.get_or(name, fallback) -> str</c>: значение или запасная строка.</summary>
	[KernName("get_or")]
	[KernMember]
	public static string GetOr(string name, string fallback) =>
		Environment.GetEnvironmentVariable(name) ?? fallback;

	/// <summary>
	/// <c>env.set(name, value)</c>: записать переменную; <c>nil</c> удаляет её
	/// (<c>str?</c> в параметре, spec §3).
	/// </summary>
	[KernMember]
	public static void Set(string name, string? value) =>
		Environment.SetEnvironmentVariable(name, value);

	/// <summary><c>env.os</c>: <c>windows</c>, <c>linux</c>, <c>macos</c> или <c>freebsd</c>.</summary>
	[KernProperty]
	public static string Os() => OperatingSystem.IsWindows()
		? "windows"
		: OperatingSystem.IsLinux()
			? "linux"
			: OperatingSystem.IsMacOS()
				? "macos"
				: OperatingSystem.IsFreeBSD() ? "freebsd" : "unknown";

	/// <summary><c>env.cwd</c>: текущий рабочий каталог процесса.</summary>
	[KernProperty]
	public static string Cwd() => Environment.CurrentDirectory;

	/// <summary><c>env.home</c>: домашний каталог пользователя.</summary>
	[KernProperty]
	public static string Home() =>
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
