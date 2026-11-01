using Kern.Binding.Attributes;

namespace Kern.Cli.Build;

/// <summary>
/// Нативные функции, которые нужны <c>build.kern</c>, но не входят в std: сейчас только чтение
/// переменной окружения (<c>w.env("NAME")</c>, docs/BUILDER.md §4). Модуль подключается
/// исключительно при компиляции сценария сборки, поэтому в обычных программах его имён нет.
/// </summary>
[KernModule("kern.build")]
public static class BuildNativeModule
{
	/// <summary><c>__build_env(name)</c>: значение переменной окружения процесса или <c>nil</c>.</summary>
	[KernName("__build_env")]
	[KernMember]
	public static string? Environment(string name) => System.Environment.GetEnvironmentVariable(name);
}
