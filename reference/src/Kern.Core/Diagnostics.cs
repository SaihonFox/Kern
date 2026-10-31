using System.Collections;

namespace Kern.Core;

public readonly record struct TextSpan(int Start, int Length)
{
	public int End => Start + Length;
	public static TextSpan FromBounds(int start, int end) => new(start, end - start);
	public static TextSpan Cover(TextSpan a, TextSpan b) => FromBounds(Math.Min(a.Start, b.Start), Math.Max(a.End, b.End));
}

public sealed class SourceFile
{
	private readonly List<int> _lineStarts = [0];

	public SourceFile(string path, string text, string? projectName = null)
	{
		Path = path;
		Text = text;
		ProjectName = projectName;
		for (int i = 0; i < text.Length; i++)
			if (text[i] == '\n') _lineStarts.Add(i + 1);
	}

	public string Path { get; }
	public string Text { get; }
	public string? ProjectName { get; }

	public (int Line, int Column) GetLineColumn(int offset)
	{
		int line = _lineStarts.BinarySearch(offset);
		if (line < 0) line = ~line - 1;
		return (line + 1, offset - _lineStarts[line] + 1);
	}

	public string GetLineText(int line)
	{
		int start = _lineStarts[line - 1];
		int end = line < _lineStarts.Count ? _lineStarts[line] : Text.Length;
		return Text[start..end].TrimEnd('\r', '\n');
	}
}

public enum Severity { Info, Warning, Error }

public sealed record Diagnostic(Severity Severity, string Code, string Message, SourceFile? File, TextSpan Span)
{
	public override string ToString()
	{
		if (File is null) return $"{Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
		var (l, c) = File.GetLineColumn(Span.Start);
		return $"{File.Path}:{l}:{c}: {Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
	}
}

public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
	private readonly List<Diagnostic> _items = [];

	public bool HasErrors => _items.Any(d => d.Severity == Severity.Error);
	public int Count => _items.Count;

	public void Report(Severity severity, string code, string message, SourceFile? file, TextSpan span) =>
		_items.Add(new Diagnostic(severity, code, message, file, span));

	public void Error(string code, string message, SourceFile? file, TextSpan span) => Report(Severity.Error, code, message, file, span);
	public void Warning(string code, string message, SourceFile? file, TextSpan span) => Report(Severity.Warning, code, message, file, span);
	public void AddRange(IEnumerable<Diagnostic> other) => _items.AddRange(other);

	public IEnumerator<Diagnostic> GetEnumerator() => _items.GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Исключение для ошибок компиляции, которые невозможно продолжить.</summary>
public sealed class KernCompileException(Diagnostic diagnostic) : Exception(diagnostic.ToString())
{
	public Diagnostic Diagnostic { get; } = diagnostic;
}
