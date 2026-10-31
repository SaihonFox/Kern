using Xunit;

namespace Kern.Tests.Cli;

/// <summary>
/// Тесты, подменяющие глобальный <see cref="Console"/>, не должны выполняться параллельно
/// с другими наборами: иначе чужой вывод попадает в захваченный поток и ожидаемый текст
/// «плывёт» (найдено 2026-10-31 на <c>Run_passes_trailing_arguments_to_main_as_string_list</c>).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCaptureCollection
{
	public const string Name = "console-capture";
}
