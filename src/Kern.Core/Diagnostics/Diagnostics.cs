namespace Kern.Core.Diagnostics;

public readonly record struct TextSpan(int Start, int Length)
{
	public int End => Start + Length;
}

/// <summary>Важность диагностики. По умолчанию все существующие диагностики — ошибки.</summary>
public enum DiagnosticSeverity
{
	Error,
	Warning,
	Info
}

/// <summary>Связанное место диагностики: «объявлено здесь», «передано здесь».</summary>
public sealed record DiagnosticNote(string Message, TextSpan Span);

public sealed record Diagnostic(
	string Code,
	string Message,
	TextSpan Span,
	DiagnosticSeverity Severity = DiagnosticSeverity.Error,
	IReadOnlyList<DiagnosticNote>? Notes = null)
{
	public bool IsError => Severity == DiagnosticSeverity.Error;

	/// <summary>Связанные места (может быть пусто).</summary>
	public IReadOnlyList<DiagnosticNote> Related => Notes ?? [];
}
