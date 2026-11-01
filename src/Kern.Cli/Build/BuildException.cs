namespace Kern.Cli.Build;

/// <summary>Ошибка сборки с готовым русским текстом для stderr (без стека).</summary>
internal sealed class BuildException(string message) : Exception(message);
