namespace Kern.Lsp.Protocol;

/// <summary>
/// Перевод между абсолютным смещением в тексте и LSP-позицией (строка/символ).
/// Смещения C# и LSP совпадают: обе считают UTF-16 code units.
/// </summary>
public sealed class TextMap
{
	private readonly int _length;
	private readonly List<int> _lineStarts = [0];

	public TextMap(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		_length = text.Length;
		for (var index = 0; index < text.Length; index++)
		{
			if (text[index] == '\n')
				_lineStarts.Add(index + 1);
		}
	}

	public (int Line, int Character) Position(int offset)
	{
		offset = System.Math.Clamp(offset, 0, _length);
		int line = _lineStarts.BinarySearch(offset);
		if (line < 0)
			line = ~line - 1;
		return (line, offset - _lineStarts[line]);
	}

	public int Offset(int line, int character)
	{
		if (line < 0)
			return 0;
		if (line >= _lineStarts.Count)
			return _length;
		return System.Math.Min(_lineStarts[line] + System.Math.Max(0, character), _length);
	}

	/// <summary>LSP-диапазон по началу и длине (для <c>TextSpan</c>).</summary>
	public object Range(int start, int length)
	{
		var from = Position(start);
		var to = Position(start + System.Math.Max(0, length));
		return new
		{
			start = new { line = from.Line, character = from.Character },
			end = new { line = to.Line, character = to.Character },
		};
	}
}
