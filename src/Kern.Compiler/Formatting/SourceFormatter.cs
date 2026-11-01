using System.Text;
using Kern.Compiler.Lexing;

namespace Kern.Compiler.Formatting;

/// <summary>
/// <c>kernc fmt</c> (spec §16, раунд 6/11): приводит файл к табам и CRLF, убирает хвостовые
/// пробелы и лишние пустые строки. Форматирование консервативное: отступы не пересчитываются
/// по скобкам, поэтому многострочные выражения и строки не ломаются. Перед записью
/// сравнивается последовательность токенов до/после — при любом расхождении файл не трогается.
/// </summary>
public static class SourceFormatter
{
	/// <summary>Отформатировать текст; при расхождении токенов возвращается исходный текст.</summary>
	public static string Format(string source)
	{
		ArgumentNullException.ThrowIfNull(source);
		string formatted = Normalize(source);
		return TokensEqual(source, formatted) ? formatted : source;
	}

	/// <summary>Изменился ли текст после <see cref="Format"/>.</summary>
	public static bool Differs(string source) => !string.Equals(Format(source), source, StringComparison.Ordinal);

	private static string Normalize(string source)
	{
		string[] lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		var builder = new StringBuilder(source.Length);
		var blank = false;
		for (var index = 0; index < lines.Length; index++)
		{
			string line = lines[index].TrimEnd();
			if (line.Length == 0)
			{
				if (blank || builder.Length == 0)
					continue;
				blank = true;
				builder.Append("\r\n");
				continue;
			}

			blank = false;
			builder.Append(Reindent(line)).Append("\r\n");
		}

		if (builder.Length == 0)
			return string.Empty;
		// Ровно один перевод строки в конце, без хвостовых пустых строк.
		return builder.ToString().TrimEnd('\r', '\n') + "\r\n";
	}

	/// <summary>Ведущие группы из четырёх пробелов заменяются табами; остальное не трогаем.</summary>
	private static string Reindent(string line)
	{
		int spaces = 0;
		while (spaces < line.Length && line[spaces] == ' ')
			spaces++;
		if (spaces == 0)
			return line;
		int tabs = spaces / 4;
		int rest = spaces % 4;
		return new string('\t', tabs) + new string(' ', rest) + line[spaces..];
	}

	private static bool TokensEqual(string before, string after)
	{
		(IReadOnlyList<Token> Tokens, IReadOnlyList<Kern.Core.Diagnostics.Diagnostic> Diagnostics) left = Lexer.Lex(before);
		(IReadOnlyList<Token> Tokens, IReadOnlyList<Kern.Core.Diagnostics.Diagnostic> Diagnostics) right = Lexer.Lex(after);
		if (left.Tokens.Count != right.Tokens.Count)
			return false;
		for (var index = 0; index < left.Tokens.Count; index++)
			if (left.Tokens[index].Kind != right.Tokens[index].Kind ||
				!string.Equals(left.Tokens[index].Text, right.Tokens[index].Text, StringComparison.Ordinal))
				return false;
		return true;
	}
}
