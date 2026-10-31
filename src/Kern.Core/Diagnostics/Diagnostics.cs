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

public sealed record Diagnostic(
	string Code,
	string Message,
	TextSpan Span,
	DiagnosticSeverity Severity = DiagnosticSeverity.Error)
{
	public bool IsError => Severity == DiagnosticSeverity.Error;
}
