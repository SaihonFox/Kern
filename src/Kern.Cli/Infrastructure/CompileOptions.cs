namespace Kern.Cli.Infrastructure;

/// <summary>
/// Настройки компиляции из <c>build.kern</c> (§7a) и флагов CLI. Сейчас реально влияет только
/// <see cref="Optimize"/>; <see cref="WarningsAsErrors"/> проверяет CLI, остальные поля входят в
/// хеш, чтобы смена настроек пересобирала проект.
/// </summary>
internal sealed record CompileOptions
{
	public int Optimize { get; init; } = 1;

	public bool WarningsAsErrors { get; init; }

	public bool Macros { get; init; } = true;

	public bool DebugInfo { get; init; } = true;

	public static CompileOptions Default { get; } = new();
}
