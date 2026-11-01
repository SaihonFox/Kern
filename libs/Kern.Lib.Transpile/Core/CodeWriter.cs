using System.Text;

namespace Kern.Lib.Transpile.Core;

/// <summary>Построчный писатель с отступами (табы).</summary>
public sealed class CodeWriter
{
	private readonly StringBuilder _text = new();

	public int Level { get; set; }

	public bool IsEmpty => _text.Length == 0;

	public void Line(string text = "")
	{
		if (text.Length == 0)
		{
			_text.Append('\n');
			return;
		}

		// Многострочные вставки (лямбды, блоки) выравниваются по текущему уровню.
		foreach (string part in text.Split('\n'))
			_text.Append('\t', Level).Append(part.TrimEnd('\r')).Append('\n');
	}

	/// <summary>Открывает блок: <c>header {</c>.</summary>
	public void Open(string header)
	{
		Line(header.Length == 0 ? "{" : header + " {");
		Level++;
	}

	/// <summary><c>} header {</c> — продолжение цепочки (<c>else</c>, <c>finally</c>).</summary>
	public void CloseOpen(string header)
	{
		Level--;
		Line("} " + header + " {");
		Level++;
	}

	public void Close(string tail = "")
	{
		Level--;
		Line("}" + tail);
	}

	public override string ToString() => _text.ToString();
}
