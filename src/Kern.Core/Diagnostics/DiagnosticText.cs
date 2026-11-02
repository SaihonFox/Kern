using Kern.Core.Text;

namespace Kern.Core.Diagnostics;

/// <summary>
/// Печать диагностики мультифайлового модуля в формате
/// <c>путь:строка:колонка: ошибка|предупреждение CODE: текст</c>: карта <see cref="SourceBundle"/>
/// переводит общее смещение в путь и локальные строку/колонку файла.
/// </summary>
public static class DiagnosticText
{
	public static string Format(Diagnostic diagnostic, SourceBundle bundle)
	{
		ArgumentNullException.ThrowIfNull(diagnostic);
		ArgumentNullException.ThrowIfNull(bundle);
		return FormatLines(diagnostic, bundle).First();
	}

	/// <summary>Диагностика вместе со связанными местами, по одной строке на место.</summary>
	public static IReadOnlyList<string> FormatLines(Diagnostic diagnostic, SourceBundle bundle)
	{
		ArgumentNullException.ThrowIfNull(diagnostic);
		ArgumentNullException.ThrowIfNull(bundle);

		var (path, line, column) = bundle.GetLineColumn(diagnostic.Span.Start);
		string kind = diagnostic.IsError ? "ошибка" : "предупреждение";
		List<string> lines =
		[
			$"{path}:{line}:{column}: {kind} {diagnostic.Code}: {diagnostic.Message}"
		];
		foreach (DiagnosticNote note in diagnostic.Related)
		{
			var (notePath, noteLine, noteColumn) = bundle.GetLineColumn(note.Span.Start);
			lines.Add($"{notePath}:{noteLine}:{noteColumn}: примечание: {note.Message}");
		}
		return lines;
	}
}
