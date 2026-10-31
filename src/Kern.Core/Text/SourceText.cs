using Kern.Core.Diagnostics;

namespace Kern.Core.Text;

/// <summary>
/// Исходный текст и карта его строк. Строки разделяются "\n", "\r\n" или одиночным "\r";
/// Unicode-разделители строк (U+2028/U+2029) строками не считаются.
/// </summary>
public sealed class SourceText
{
	private readonly List<int> _lineStarts;

	public SourceText(string text, string path = "")
	{
		ArgumentNullException.ThrowIfNull(text);
		Text = text;
		Path = path ?? string.Empty;
		_lineStarts = [0];
		for (var index = 0; index < text.Length; index++)
		{
			var symbol = text[index];
			if (symbol == '\r')
			{
				if (index + 1 < text.Length && text[index + 1] == '\n')
					index++;
				_lineStarts.Add(index + 1);
			}
			else if (symbol == '\n')
			{
				_lineStarts.Add(index + 1);
			}
		}
	}

	public string Text { get; }

	public string Path { get; }

	public int Length => Text.Length;

	/// <summary>Число строк; пустой текст — одна строка.</summary>
	public int LineCount => _lineStarts.Count;

	/// <summary>Номер строки (1-based) для смещения; смещение зажимается в [0, Length].</summary>
	public int GetLine(int offset)
	{
		var clamped = Math.Clamp(offset, 0, Text.Length);
		var lineIndex = _lineStarts.BinarySearch(clamped);
		return (lineIndex >= 0 ? lineIndex : ~lineIndex - 1) + 1;
	}

	/// <summary>Строка и колонка (обе 1-based) для смещения.</summary>
	public (int Line, int Column) GetLineColumn(int offset)
	{
		var clamped = Math.Clamp(offset, 0, Text.Length);
		var line = GetLine(clamped);
		return (line, clamped - _lineStarts[line - 1] + 1);
	}

	/// <summary>Участок строки без символов перевода строки.</summary>
	public TextSpan LineSpan(int line)
	{
		if (line < 1 || line > _lineStarts.Count)
			throw new ArgumentOutOfRangeException(nameof(line), line, "строка вне текста");

		var start = _lineStarts[line - 1];
		var end = line < _lineStarts.Count ? _lineStarts[line] : Text.Length;
		while (end > start && (Text[end - 1] == '\n' || Text[end - 1] == '\r'))
			end--;
		return new TextSpan(start, end - start);
	}
}
