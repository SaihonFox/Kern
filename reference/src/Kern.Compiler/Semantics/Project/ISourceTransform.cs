using Kern.Core;

namespace Kern.Compiler.Semantics.Project;

/// <summary>Исходный файл до разбора: путь, текст и имя модуля.</summary>
public readonly record struct SourceText(string Path, string Text, string Module);

/// <summary>
/// Преобразование исходников до разбора (раскрытие макросов). Реализация живёт в хосте (kernc): ей нужны VM и кодогенератор.
/// Диагностики кладутся в <paramref name="diagnostics"/> — они попадут в общий список проекта.
/// </summary>
public interface ISourceTransform
{
	IReadOnlyList<SourceText> Transform(string projectName, IReadOnlyList<SourceText> files, DiagnosticBag diagnostics);
}
