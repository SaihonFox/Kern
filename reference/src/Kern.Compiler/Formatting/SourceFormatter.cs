using Kern.Compiler.Lexing;
using Kern.Compiler.Parsing;
using Kern.Core;

namespace Kern.Compiler.Formatting;

/// <summary>Результат форматирования: либо новый текст, либо сообщение, почему файл не тронут.</summary>
public sealed record FormatResult(string? Text, string? Error);

/// <summary>Стиль вывода: по умолчанию табы и CRLF.</summary>
public sealed record FormatOptions(bool UseTabs, string NewLine)
{
	public static FormatOptions Default { get; } = new(true, "\r\n");

	public string Indent(int level) => UseTabs ? new string('\t', level) : new string(' ', level * 4);
}

/// <summary>
/// Нормализация отступов и пробелов. Опирается на токены лексера и проверку парсером (код с ошибками не форматируется):
/// <list type="bullet">
/// <item>отступ — один таб (или 4 пробела, см. <see cref="FormatOptions"/>) на уровень скобок `() [] {}`; строка, начинающаяся с закрывающей скобки, выравнивается с открывшей;</item>
/// <item>продолжения без скобок (тело ветки на следующей строке, `.цепочка`, операнд после оператора в конце строки) определяет парсер и сдвигает на уровень глубже;</item>
/// <item>хвостовые пробелы убираются, отступ нормализуется, подряд идущие пустые строки сжимаются до одной, файл заканчивается одним переводом строки (по умолчанию CRLF);</item>
/// <item>комментарии остаются на месте; многострочные строки и блочные комментарии не трогаются.</item>
/// </list>
/// Перед выдачей результата токены до и после сравниваются: форматирование не может изменить программу.
/// </summary>
public static class SourceFormatter
{
	private const int TabWidth = 4;

	public static FormatResult Format(string path, string text, FormatOptions? options = null)
	{
		options ??= FormatOptions.Default;
		var bag = new DiagnosticBag();
		string normalized = text.Replace("\r\n", "\n");
		ParserFacade.ParseForFormatting(path, normalized, bag, out var continuation);
		if (bag.HasErrors) return new(null, "в файле синтаксические ошибки: " + bag.First(d => d.Severity == Severity.Error));

		var lines = normalized.Split('\n');

		var file = new SourceFile(path, normalized);
		var tokens = Lexer.Lex(file, new DiagnosticBag()).Where(t => t.Kind is not (TokenKind.Eof or TokenKind.NewLine)).ToList();

		var info = Analyze(file, tokens, lines.Length);
		var output = new List<string>(lines.Length);
		var stacks = new List<Stack<int>> { new() };
		int depth = 0;
		bool prevBlank = true; // убираем пустые строки в начале файла

		for (int i = 0; i < lines.Length; i++)
		{
			string line = lines[i];
			if (info.Verbatim[i]) { output.Add(line.TrimEnd('\r')); prevBlank = false; depth += info.Delta[i]; continue; }

			string trimmed = line.Trim();
			if (trimmed.Length == 0)
			{
				if (!prevBlank) output.Add("");
				prevBlank = true;
				continue;
			}
			prevBlank = false;

			int lineDepth = Math.Max(0, depth - info.LeadingClosers[i]);
			while (stacks.Count <= lineDepth) stacks.Add(new());
			var stack = stacks[lineDepth];
			int extra = 0;
			if (continuation.Contains(i) && info.LeadingClosers[i] == 0)
			{
				// продолжение: уровень зависит от относительного сдвига среди соседних продолжений
				int orig = OriginalIndent(line);
				while (stack.Count > 0 && orig < stack.Peek()) stack.Pop();
				if (stack.Count == 0 || orig > stack.Peek()) stack.Push(orig);
				extra = stack.Count;
			}
			else stack.Clear();

			output.Add(options.Indent((lineDepth + extra)) + trimmed);

			int newDepth = Math.Max(0, depth + info.Delta[i]);
			if (newDepth > depth)
				for (int d = depth + 1; d <= newDepth; d++)
				{
					while (stacks.Count <= d) stacks.Add(new());
					stacks[d].Clear();
				}
			depth = newDepth;
		}

		while (output.Count > 0 && output[^1].Length == 0) output.RemoveAt(output.Count - 1);
		string result = string.Join(options.NewLine, output) + options.NewLine;

		// страховка: токены не должны измениться
		var after = Lexer.Lex(new SourceFile(path, result), new DiagnosticBag()).Where(t => t.Kind is not (TokenKind.Eof or TokenKind.NewLine)).ToList();
		if (after.Count != tokens.Count || after.Zip(tokens).Any(p => p.First.Kind != p.Second.Kind || p.First.Text != p.Second.Text))
			return new(null, "внутренняя ошибка форматтера: токены изменились — файл не тронут");
		return new(result, null);
	}

	private static int OriginalIndent(string line)
	{
		int n = 0;
		foreach (char c in line)
		{
			if (c == ' ') n++;
			else if (c == '\t') n += TabWidth;
			else break;
		}
		return n;
	}

	private sealed record LineInfo(bool[] Verbatim, int[] Delta, int[] LeadingClosers);

	private static LineInfo Analyze(SourceFile file, List<Token> tokens, int lineCount)
	{
		var verbatim = new bool[lineCount];
		var delta = new int[lineCount];
		var leading = new int[lineCount];
		var stillLeading = new bool[lineCount];
		Array.Fill(stillLeading, true);

		int prevEnd = 0;
		foreach (var t in tokens)
		{
			MarkBlockComments(file, prevEnd, t.Span.Start, verbatim);
			prevEnd = t.Span.End;

			int startLine = file.GetLineColumn(t.Span.Start).Line - 1;
			int endLine = file.GetLineColumn(Math.Max(t.Span.Start, t.Span.End - 1)).Line - 1;
			for (int l = startLine + 1; l <= endLine && l < lineCount; l++) verbatim[l] = true;

			bool closer = t.Kind is TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace;
			bool opener = t.Kind is TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace
				or TokenKind.DollarBracket or TokenKind.HashBracket;
			if (opener) delta[startLine]++;
			if (closer)
			{
				delta[startLine]--;
				if (stillLeading[startLine]) leading[startLine]++;
			}
			else stillLeading[startLine] = false;
		}
		MarkBlockComments(file, prevEnd, file.Text.Length, verbatim);
		return new(verbatim, delta, leading);
	}

	/// <summary>В промежутке между токенами только пробелы и комментарии: строки внутри `/* … */` не трогаем.</summary>
	private static void MarkBlockComments(SourceFile file, int from, int to, bool[] verbatim)
	{
		var text = file.Text;
		for (int i = from; i < to - 1; i++)
		{
			if (text[i] == '/' && text[i + 1] == '/')
			{
				while (i < to && text[i] != '\n') i++;
			}
			else if (text[i] == '/' && text[i + 1] == '*')
			{
				int start = file.GetLineColumn(i).Line - 1;
				int j = i + 2;
				while (j < to - 1 && !(text[j] == '*' && text[j + 1] == '/')) j++;
				int end = file.GetLineColumn(Math.Min(j + 1, text.Length - 1)).Line - 1;
				for (int l = start + 1; l <= end && l < verbatim.Length; l++) verbatim[l] = true;
				i = j + 1;
			}
		}
	}
}
