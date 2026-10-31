using System.Collections.ObjectModel;
using System.Text;

namespace Kern.Core.Text;

/// <summary>
/// Несколько файлов проекта как один модуль: тексты склеиваются через "\n" в
/// <see cref="Text"/>, а карта смещений возвращает исходный файл и локальные строку/колонку.
/// Все стадии компиляции работают с общим текстом без изменений; карта нужна только
/// диагностике и таблицам строк кода.
/// </summary>
/// <remarks>
/// Границы файлов: файл <c>i</c> начинается со смещения <c>start[i]</c>, эти смещения строго
/// возрастают, поэтому смещение относится к последнему файлу, у которого
/// <c>start[i] &lt;= offset</c>. Отсюда: символ-разделитель "\n" между файлами принадлежит
/// предыдущему файлу (он не входит в текст следующего), а первое смещение следующего файла —
/// уже следующему. Пустой файл не содержит текста и владеет только собственным <c>start</c>
/// (если за ним идёт ещё один файл). Смещения вне текста зажимаются в
/// <c>[0, Text.Length]</c>. Переводы строк внутри файлов понимаются ровно так же, как в
/// <see cref="SourceText"/> ("\n", "\r\n", "\r"); хвостовой перевод строки файла не сдвигает
/// нумерацию следующего файла.
/// </remarks>
public sealed class SourceBundle
{
	private readonly ReadOnlyCollection<string> _paths;
	private readonly int[] _starts;

	/// <summary>Номер первой строки файла внутри <see cref="Text"/> (1-based).</summary>
	private readonly int[] _startLines;

	private SourceBundle(string[] paths, int[] starts, SourceText text)
	{
		_paths = Array.AsReadOnly(paths);
		_starts = starts;
		Text = text;
		_startLines = new int[starts.Length];
		for (var index = 0; index < starts.Length; index++)
			_startLines[index] = text.GetLine(starts[index]);
	}

	/// <summary>
	/// Склеенный текст модуля. <see cref="SourceText.Path"/> — путь первого файла: именно его
	/// видят стадии, которые по-прежнему принимают один <see cref="SourceText"/>.
	/// </summary>
	public SourceText Text { get; }

	/// <summary>Пути файлов в порядке склейки (в том виде, как их передал вызывающий).</summary>
	public IReadOnlyList<string> Paths => _paths;

	/// <summary>Число файлов; всегда больше нуля.</summary>
	public int FileCount => _paths.Count;

	/// <summary>
	/// Склеивает файлы в один модуль. Пути не нормализуются (вызывающий передаёт пути
	/// проекта), тексты <c>null</c> быть не могут, пустой файл допустим.
	/// </summary>
	public static SourceBundle Create(IReadOnlyList<(string Path, string Text)> files)
	{
		ArgumentNullException.ThrowIfNull(files);
		if (files.Count == 0)
			throw new ArgumentException("нужен хотя бы один файл", nameof(files));

		var paths = new string[files.Count];
		var starts = new int[files.Count];
		var builder = new StringBuilder();
		for (var index = 0; index < files.Count; index++)
		{
			var (path, text) = files[index];
			ArgumentNullException.ThrowIfNull(path);
			ArgumentNullException.ThrowIfNull(text);
			if (index > 0)
				builder.Append('\n');
			paths[index] = path;
			starts[index] = builder.Length;
			builder.Append(text);
		}

		return new SourceBundle(paths, starts, new SourceText(builder.ToString(), paths[0]));
	}

	/// <summary>Файл, строка и колонка (строка/колонка 1-based и локальны для файла).</summary>
	public (string Path, int Line, int Column) GetLineColumn(int offset)
	{
		var index = FileIndexAt(offset);
		var (line, column) = Text.GetLineColumn(offset);
		return (_paths[index], line - LineOffsetOf(index), column);
	}

	/// <summary>Путь файла, которому принадлежит смещение (правило границы — в remarks класса).</summary>
	public string PathAt(int offset) => _paths[FileIndexAt(offset)];

	/// <summary>Смещение строк содержащего файла: локальная строка = <c>Text.GetLine(offset) - LineOffset(offset)</c>.</summary>
	public int LineOffset(int offset) => LineOffsetOf(FileIndexAt(offset));

	/// <summary>Локальная строка (1-based) файла для смещения.</summary>
	public int GetLocalLine(int offset) => Text.GetLine(offset) - LineOffset(offset);

	private int LineOffsetOf(int index) => _startLines[index] - 1;

	/// <summary>Индекс файла по правилу границы: последний файл, чей <c>start</c> не больше смещения.</summary>
	private int FileIndexAt(int offset)
	{
		var clamped = Math.Clamp(offset, 0, Text.Length);
		var low = 0;
		var high = _starts.Length - 1;
		var found = 0;
		while (low <= high)
		{
			var middle = low + ((high - low) / 2);
			if (_starts[middle] <= clamped)
			{
				found = middle;
				low = middle + 1;
			}
			else
			{
				high = middle - 1;
			}
		}

		return found;
	}
}
