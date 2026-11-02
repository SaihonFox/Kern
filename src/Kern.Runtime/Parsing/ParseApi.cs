using System.Globalization;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Vm.Execution;

namespace Kern.Runtime.Parsing;

/// <summary>
/// <c>kern.parse</c>: базовые строительные блоки разбора текста и DSL — проверка
/// идентификатора/числа/строки, разэкранирование и диагностика с позицией и
/// подчёркиванием. Полные комбинаторы (<c>digit ~&gt; many</c>, PEG из текста) — следующий
/// срез: см. docs/LIBRARIES.md.
/// </summary>
[KernModule("kern.parse")]
public static class ParseApi
{
	/// <summary><c>parse_ident(text)</c>: весь текст — идентификатор Kern.</summary>
	[KernMember]
	public static KResult<string, string> ParseIdent(string text)
	{
		if (text.Length == 0)
			return Fail("ожидался идентификатор");
		if (!IsIdentStart(text[0]))
			return Fail($"недопустимый первый символ '{text[0]}'");
		for (int index = 1; index < text.Length; index++)
			if (!IsIdentPart(text[index]))
				return Fail($"недопустимый символ '{text[index]}' в позиции {index}");
		return KResult<string, string>.Success(text);
	}

	/// <summary><c>parse_integer(text)</c>: целое i64 (необязательный знак).</summary>
	[KernMember]
	public static KResult<long, string> ParseInteger(string text)
	{
		if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
			return Fail<long>("ожидалось целое i64");
		return KResult<long, string>.Success(value);
	}

	/// <summary><c>parse_number(text)</c>: дробное f64 (инвариантная культура).</summary>
	[KernMember]
	public static KResult<double, string> ParseNumber(string text)
	{
		if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
			!double.IsFinite(value))
			return Fail<double>("ожидалось конечное число f64");
		return KResult<double, string>.Success(value);
	}

	/// <summary><c>parse_quoted(text)</c>: строка в кавычках с escape-последовательностями.</summary>
	[KernMember]
	public static KResult<string, string> ParseQuoted(string text)
	{
		if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
			return Fail("ожидалась строка в двойных кавычках");
		var builder = new StringBuilder(text.Length - 2);
		for (int index = 1; index < text.Length - 1; index++)
		{
			char symbol = text[index];
			if (symbol != '\\')
			{
				builder.Append(symbol);
				continue;
			}
			if (++index >= text.Length - 1)
				return Fail("незавершённая escape-последовательность");
			char escape = text[index];
			switch (escape)
			{
				case 'n': builder.Append('\n'); break;
				case 't': builder.Append('\t'); break;
				case 'r': builder.Append('\r'); break;
				case '\\': builder.Append('\\'); break;
				case '"': builder.Append('"'); break;
				case 'u':
					if (index + 4 >= text.Length - 1 ||
						!ushort.TryParse(text.AsSpan(index + 1, 4), NumberStyles.HexNumber,
							CultureInfo.InvariantCulture, out ushort code))
						return Fail("ожидалось \\uNNNN");
					builder.Append((char)code);
					index += 4;
					break;
				default:
					return Fail($"неизвестная escape-последовательность '\\{escape}'");
			}
		}
		return KResult<string, string>.Success(builder.ToString());
	}

	/// <summary>
	/// <c>parse_error(text, offset)</c>: многострочная диагностика с номером строки/колонки
	/// и подчёркиванием проблемного места (для сообщений компилятора/DSL).
	/// </summary>
	[KernMember]
	public static string ParseError(string text, int offset)
	{
		ArgumentNullException.ThrowIfNull(text);
		int position = System.Math.Clamp(offset, 0, text.Length);
		int lineStart = text.LastIndexOf('\n', System.Math.Max(0, position - 1));
		lineStart = lineStart < 0 ? 0 : lineStart + 1;
		int lineEnd = text.IndexOf('\n', position);
		lineEnd = lineEnd < 0 ? text.Length : lineEnd;
		int line = 1;
		for (int index = 0; index < lineStart; index++)
			if (text[index] == '\n') line++;
		int column = position - lineStart + 1;
		string source = text[lineStart..lineEnd].TrimEnd('\r');
		return $"{line}:{column}: ошибка разбора\n  {source}\n  {new string(' ', System.Math.Max(0, column - 1))}^";
	}

	/// <summary><c>is_ident(text)</c>: проверка без результата (быстрый предикат).</summary>
	[KernName("is_ident")]
	[KernMember]
	public static bool IsIdent(string text) => ParseIdent(text).IsOk;

	private static bool IsIdentStart(char symbol) => char.IsLetter(symbol) || symbol == '_';

	private static bool IsIdentPart(char symbol) => char.IsLetterOrDigit(symbol) || symbol == '_';

	private static KResult<T, string> Fail<T>(string message) => KResult<T, string>.Failure(message);

	private static KResult<string, string> Fail(string message) => Fail<string>(message);
}
