using System.Text;

namespace Kern.Lib.Transpile.Core;

/// <summary>Построчный писатель с отступами (табы).</summary>
public sealed class CodeWriter
{
	private readonly StringBuilder _sb = new();
	public int Level { get; set; }

	public bool IsEmpty => _sb.Length == 0;

	public void Line(string text = "")
	{
		if (text.Length == 0) { _sb.Append('\n'); return; }
		// многострочные вставки (лямбды) выравниваются по текущему уровню
		foreach (var part in text.Split('\n'))
		{
			_sb.Append('\t', Level).Append(part).Append('\n');
		}
	}

	/// <summary>Открывает блок: `header {`.</summary>
	public void Open(string header)
	{
		Line(header.Length == 0 ? "{" : header + " {");
		Level++;
	}

	/// <summary>`} header {` — продолжение цепочки (else / else if / finally).</summary>
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

	public override string ToString() => _sb.ToString();
}
