namespace Kern.Compiler.Lexing;

/// <summary>
/// Заголовок строкового литерала: префикс, вид кавычек и границы содержимого в тексте токена.
/// Lexer хранит в токене исходный текст вместе с префиксом и кавычками (spec §3).
/// </summary>
public readonly record struct StringLiteralHeader(
	string Prefix,
	bool Interpolated,
	bool Raw,
	bool Verbatim,
	bool Regex,
	int QuoteIndex,
	bool Triple,
	int ContentStart,
	int ContentEnd);

/// <summary>
/// Снятие кавычек и escape-последовательностей со строковых и char-литералов.
/// Lexer хранит в токене исходный текст вместе с префиксом и кавычками (spec §3),
/// binder превращает его в значение. Интерполяция строк разбирается парсером.
/// </summary>
public static class StringLiteralText
{
	/// <summary>Декодировать строковый литерал. <paramref name="error"/> заполняется по-русски.</summary>
	public static bool TryDecode(string token, out string value, out string? error)
	{
		value = string.Empty;
		error = null;

		if (!TryReadHeader(token, out var header, out error))
			return false;

		if (header.Regex)
		{
			error = "regex-литерал 'x\"…\"' пока не поддержан (M7)";
			return false;
		}

		if (header.Interpolated)
		{
			error = "интерполяция строк 'f\"…\"' пока не поддержана (следующий срез M3)";
			return false;
		}

		var content = token[header.ContentStart..header.ContentEnd];
		return TryDecodeInterpolationText(content, header.Raw, header.Verbatim, out value, out error);
	}

	/// <summary>
	/// Декодировать кусок текста интерполированной строки: <c>r</c> сохраняет обратные слэши,
	/// <c>v</c> превращает <c>""</c> в кавычку, обычная строка снимает escape-последовательности.
	/// </summary>
	public static bool TryDecodeInterpolationText(
		string content,
		bool raw,
		bool verbatim,
		out string value,
		out string? error)
	{
		error = null;
		if (verbatim)
		{
			value = content.Replace("\"\"", "\"", StringComparison.Ordinal);
			return true;
		}

		if (raw)
		{
			value = content;
			return true;
		}

		return TryUnescape(content, out value, out error);
	}

	/// <summary>Разобрать префикс и кавычки строкового токена.</summary>
	public static bool TryReadHeader(string token, out StringLiteralHeader header, out string? error)
	{
		header = default;
		error = null;

		var quote = token.IndexOf('"');
		if (quote < 0)
		{
			error = "не строковый литерал";
			return false;
		}

		var prefix = token[..quote];
		if (!IsKnownPrefix(prefix, out var interpolated, out var raw, out var verbatim, out var regex))
		{
			error = $"неизвестный префикс строки '{prefix}'";
			return false;
		}

		var triple = token.AsSpan(quote).StartsWith("\"\"\"", StringComparison.Ordinal);
		var delimiter = triple ? 3 : 1;
		var contentStart = quote + delimiter;
		var contentEnd = token.Length - delimiter;
		if (token.Length < contentStart + delimiter || contentEnd < contentStart ||
			!token.AsSpan(contentEnd).StartsWith("\"\"\"".AsSpan(0, delimiter), StringComparison.Ordinal))
		{
			error = "незакрытый строковый литерал";
			return false;
		}

		header = new StringLiteralHeader(
			prefix, interpolated, raw, verbatim, regex, quote, triple, contentStart, contentEnd);
		return true;
	}

	/// <summary>Декодировать char-литерал вида <c>'a'</c>, <c>'\n'</c>, <c>'\u4564'</c>.</summary>
	public static bool TryDecodeChar(string token, out char value, out string? error)
	{
		value = '\0';
		error = null;
		if (token.Length < 3 || token[0] != '\'' || token[^1] != '\'')
		{
			error = "не char-литерал";
			return false;
		}

		var content = token[1..^1];
		if (content.Length == 1 && content[0] != '\\')
		{
			value = content[0];
			return true;
		}

		var index = 0;
		if (!TryReadEscape(content, ref index, out value, out error))
			return false;
		if (index != content.Length)
		{
			error = "char-литерал должен содержать ровно один символ";
			return false;
		}

		return true;
	}

	private static bool IsKnownPrefix(
		string prefix,
		out bool interpolated,
		out bool raw,
		out bool verbatim,
		out bool regex)
	{
		interpolated = false;
		raw = false;
		verbatim = false;
		regex = false;
		foreach (var symbol in prefix)
		{
			switch (symbol)
			{
				case 'f': interpolated = true; break;
				case 'r': raw = true; break;
				case 'v': verbatim = true; break;
				case 'x': regex = true; break;
				default: return false;
			}
		}

		return !(raw && verbatim);
	}

	private static bool TryUnescape(string content, out string value, out string? error)
	{
		value = string.Empty;
		error = null;
		var builder = new System.Text.StringBuilder(content.Length);
		var index = 0;
		while (index < content.Length)
		{
			if (content[index] != '\\')
			{
				builder.Append(content[index++]);
				continue;
			}

			if (!TryReadEscape(content, ref index, out var symbol, out error))
				return false;
			builder.Append(symbol);
		}

		value = builder.ToString();
		return true;
	}

	private static bool TryReadEscape(string content, ref int index, out char value, out string? error)
	{
		value = '\0';
		error = null;
		if (index >= content.Length || content[index] != '\\')
		{
			error = "ожидалась escape-последовательность";
			return false;
		}

		if (index + 1 >= content.Length)
		{
			error = "escape-последовательность оборвана";
			return false;
		}

		var symbol = content[index + 1];
		index += 2;
		switch (symbol)
		{
			case '0': value = '\0'; return true;
			case 'a': value = '\a'; return true;
			case 'b': value = '\b'; return true;
			case 'f': value = '\f'; return true;
			case 'n': value = '\n'; return true;
			case 'r': value = '\r'; return true;
			case 't': value = '\t'; return true;
			case 'v': value = '\v'; return true;
			case '\\': value = '\\'; return true;
			case '\'': value = '\''; return true;
			case '"': value = '"'; return true;
			// `\${` в интерполированной строке даёт литерал `${` (spec §3).
			case '$': value = '$'; return true;
			case 'u':
				if (!TryReadUnicode(content, ref index, out value))
				{
					error = "в \\u escape нужны ровно четыре шестнадцатеричные цифры";
					return false;
				}
				return true;
			default:
				error = $"неизвестная escape-последовательность '\\{symbol}'";
				return false;
		}
	}

	private static bool TryReadUnicode(string content, ref int index, out char value)
	{
		value = '\0';
		if (index + 4 > content.Length)
			return false;

		var code = 0;
		for (var offset = 0; offset < 4; offset++)
		{
			var digit = content[index + offset] switch
			{
				>= '0' and <= '9' => content[index + offset] - '0',
				>= 'a' and <= 'f' => content[index + offset] - 'a' + 10,
				>= 'A' and <= 'F' => content[index + offset] - 'A' + 10,
				_ => -1
			};
			if (digit < 0)
				return false;
			code = (code << 4) | digit;
		}

		value = (char)code;
		index += 4;
		return true;
	}
}
