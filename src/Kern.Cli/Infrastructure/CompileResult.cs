using Kern.Binding.Catalog;
using Kern.Bytecode;
using Kern.Compiler.Compilation;
using Kern.Core.Diagnostics;
using Kern.Core.Text;

namespace Kern.Cli.Infrastructure;

/// <summary>Скомпилированный файл: байткод модуля и каталог нативов для запуска.</summary>
internal sealed record CompiledProgram(KbcModule Module, NativeCatalog Catalog);

/// <summary>
/// Результат компиляции одного исходника: либо готовый <see cref="Program"/>, либо
/// диагностики, либо сообщение о сбое чтения/внутренней ошибке (<see cref="FailureMessage"/>).
/// Предупреждения модуль не отменяют.
/// </summary>
internal sealed class CompileResult
{
	public CompiledProgram? Program { get; init; }

	public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

	/// <summary>Ошибки импорта нативов: строки без span'а.</summary>
	public IReadOnlyList<string> NativeProblems { get; init; } = [];

	/// <summary>Готовые строки диагностик проекта (путь/строка уже подставлены).</summary>
	public IReadOnlyList<string> ProjectMessages { get; init; } = [];

	/// <summary>Диагностики проекта до форматирования (проверка «предупреждения как ошибки»).</summary>
	public IReadOnlyList<ProjectDiagnostic> ProjectDiagnostics { get; init; } = [];

	public string? FailureMessage { get; init; }

	public SourceText? Source { get; init; }

	/// <summary>
	/// Карта файлов проекта: заполнена только в режиме папки. Диагностики печатаются по ней,
	/// чтобы путь и строка были локальны исходному файлу.
	/// </summary>
	public SourceBundle? Sources { get; init; }

	/// <summary>
	/// Исходники после раскрытия макросов (если оно выполнялось): <c>kernc dll</c> строит
	/// интерфейсные заглушки по ним, чтобы члены, созданные макросами, были видны потребителю.
	/// </summary>
	public IReadOnlyList<(string Path, string Text)>? ExpandedFiles { get; init; }

	public bool Success => Program is not null;
}
