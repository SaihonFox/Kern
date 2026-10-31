using Kern.Core.Diagnostics;

namespace Kern.Compiler.Lexing;

public sealed class Lexer
{
	private static readonly Dictionary<string, TokenKind> Keywords = new()
	{
		["fun"] = TokenKind.Fun,
		["type"] = TokenKind.Type,
		["unit"] = TokenKind.Unit,
		["use"] = TokenKind.Use,
		["loop"] = TokenKind.Loop,
		["match"] = TokenKind.Match,
		["async"] = TokenKind.Async,
		["await"] = TokenKind.Await,
		["op"] = TokenKind.Op,
		["ctor"] = TokenKind.Ctor,
		["open"] = TokenKind.Open,
		["in"] = TokenKind.In,
		["is"] = TokenKind.Is,
		["var"] = TokenKind.Var,
		["else"] = TokenKind.Else,
		["ret"] = TokenKind.Ret,
		["stop"] = TokenKind.Stop,
		["next"] = TokenKind.Next,
		["project"] = TokenKind.Project
	};

	// Порядок важен: совпадение ищется по первому префиксу, поэтому многосимвольные
	// операторы стоят раньше своих префиксов (`<<=` до `<<`/`<=`, `>>=` до `>>`/`>=`).
	private static readonly string[] Operators =
	[
		"**=", "===", "!==", "<<=", ">>=", "&=", "|=", "^=", "**", "==", "!=", "<=", ">=", "?.", "?:", "..=", "..",
		"+=", "-=", "*=", "/=", "%=", "=>", "++", "--", "&&", "||", "!in", "!is",
		"<<", ">>",
		"+", "->", "-", "*", "/", "%", "<", ">", "!", "?", "=", "&", "|", "^", "~"
	];

	private static readonly HashSet<string> StringPrefixes = ["f", "r", "x", "v", "fr", "fx", "fv"];

	public static (IReadOnlyList<Token> Tokens, IReadOnlyList<Diagnostic> Diagnostics) Lex(string source)
	{
		ArgumentNullException.ThrowIfNull(source);
		var tokens = new List<Token>();
		var diagnostics = new List<Diagnostic>();
		var position = 0;

		// Шебанг допустим только в самой первой строке файла и не попадает в токены.
		if (source.StartsWith("#!", StringComparison.Ordinal))
			while (position < source.Length && source[position] is not '\r' and not '\n')
				position++;

		while (position < source.Length)
		{
			var character = source[position];
			if (char.IsWhiteSpace(character))
			{
				position++;
				continue;
			}
			if (character == '/' && position + 1 < source.Length && source[position + 1] == '/')
			{
				position += 2;
				while (position < source.Length && source[position] is not '\r' and not '\n') position++;
				continue;
			}
			if (character == '/' && position + 1 < source.Length && source[position + 1] == '*')
			{
				var commentStart = position;
				var depth = 1;
				position += 2;
				while (position + 1 < source.Length && depth > 0)
				{
					if (source[position] == '/' && source[position + 1] == '*')
					{
						depth++;
						position += 2;
					}
					else if (source[position] == '*' && source[position + 1] == '/')
					{
						depth--;
						position += 2;
					}
					else
						position++;
				}
				if (depth > 0)
				{
					diagnostics.Add(new("LEX002", "Незакрытый многострочный комментарий", new(commentStart, source.Length - commentStart)));
					break;
				}
				continue;
			}

			var start = position;
			if (char.IsLetter(character) || character == '_')
			{
				position++;
				while (position < source.Length && (char.IsLetterOrDigit(source[position]) || source[position] == '_')) position++;
				var text = source[start..position];
				if (position < source.Length && source[position] == '"' && StringPrefixes.Contains(text))
				{
					ReadString(source, start, ref position, tokens, diagnostics);
					continue;
				}
				tokens.Add(new(Keywords.GetValueOrDefault(text, TokenKind.Identifier), text, new(start, position - start)));
				continue;
			}
			if (character == '\'')
			{
				ReadCharacter(source, start, ref position, tokens, diagnostics);
				continue;
			}
			if (character == '"')
			{
				ReadString(source, start, ref position, tokens, diagnostics);
				continue;
			}
			if (char.IsDigit(character) || (character == '.' && position + 1 < source.Length && char.IsDigit(source[position + 1])))
			{
				ReadNumber(source, start, ref position, tokens, diagnostics);
				continue;
			}

			var operatorText = Operators.FirstOrDefault(value => source.AsSpan(position).StartsWith(value, StringComparison.Ordinal));
			if (operatorText is not null)
			{
				position += operatorText.Length;
				tokens.Add(new(operatorText == "=" ? TokenKind.Equals : TokenKind.Operator, operatorText, new(start, operatorText.Length)));
				continue;
			}

			var kind = character == '.' && HasNewlineBefore(source, start)
				? TokenKind.LeadingDot
				: character switch
			{
				'@' => TokenKind.At, '#' => TokenKind.Hash, '$' => TokenKind.Dollar,
				'(' => TokenKind.LeftParen, ')' => TokenKind.RightParen, '{' => TokenKind.LeftBrace,
				'}' => TokenKind.RightBrace, '[' => TokenKind.LeftBracket, ']' => TokenKind.RightBracket,
				',' => TokenKind.Comma, ':' => TokenKind.Colon, '.' => TokenKind.Dot, ';' => TokenKind.Semicolon,
				_ => TokenKind.Unknown
			};
			position++;
			if (kind == TokenKind.Unknown)
				diagnostics.Add(new("LEX001", $"Неизвестный символ: '{character}'", new(start, 1)));
			tokens.Add(new(kind, source[start..position], new(start, 1)));
		}

		tokens.Add(new(TokenKind.EndOfFile, string.Empty, new(source.Length, 0)));
		return (tokens, diagnostics);
	}

	private static bool HasNewlineBefore(string source, int position)
	{
		for (var index = position - 1; index >= 0 && char.IsWhiteSpace(source[index]); index--)
			if (source[index] is '\r' or '\n')
				return true;
		return false;
	}

	private static void ReadNumber(string source, int start, ref int position, List<Token> tokens, List<Diagnostic> diagnostics)
	{
		var radix = 10;
		var isRadix = false;
		var malformed = false;
		var bodyStart = start;
		if (source.AsSpan(position).StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
			source.AsSpan(position).StartsWith("0b", StringComparison.OrdinalIgnoreCase) ||
			source.AsSpan(position).StartsWith("0h", StringComparison.OrdinalIgnoreCase))
		{
			isRadix = true;
			radix = char.ToLowerInvariant(source[position + 1]) switch { 'x' => 16, 'b' => 2, _ => 8 };
			position += 2;
			bodyStart = position;
			while (position < source.Length && (char.IsLetterOrDigit(source[position]) || source[position] == '_'))
				position++;
			var body = source.AsSpan(bodyStart, position - bodyStart);
			malformed = body.IsEmpty || body[0] == '_' || body[^1] == '_' ||
				body.Contains("__", StringComparison.Ordinal);
			foreach (var digit in body)
				if (digit != '_' && (DigitValue(digit) < 0 || DigitValue(digit) >= radix))
					malformed = true;
		}
		else
		{
			if (source[position] == '.') position++;
			bodyStart = position;
			while (position < source.Length && (char.IsDigit(source[position]) || source[position] == '_')) position++;
			var integerEnd = position;
			if (position < source.Length && source[position] == '.' &&
				(position + 1 >= source.Length || char.IsDigit(source[position + 1])))
			{
				position++;
				var fractionStart = position;
				while (position < source.Length && (char.IsDigit(source[position]) || source[position] == '_')) position++;
				malformed = fractionStart < position && (source[fractionStart] == '_' || source[position - 1] == '_');
			}
			var integerPart = source.AsSpan(bodyStart, integerEnd - bodyStart);
			malformed |= integerPart.Length > 0 && (integerPart[0] == '_' || integerPart[^1] == '_' ||
				integerPart.Contains("__", StringComparison.Ordinal));
		}
		var numberEnd = position;
		while (position < source.Length && char.IsLetterOrDigit(source[position])) position++;
		var suffix = source[numberEnd..position];
		if (suffix.Length > 0 && !IsNumericSuffix(suffix))
			malformed = true;
		var text = source[start..position];
		var kind = !isRadix && (suffix.Length > 0 || text.Contains('.')) ? TokenKind.NumberLiteral : TokenKind.IntegerLiteral;
		if (malformed)
			diagnostics.Add(new("LEX005", "Некорректный числовой литерал", new(start, position - start)));
		tokens.Add(new(kind, text, new(start, position - start)));
	}

	private static int DigitValue(char value) => value switch
	{
		>= '0' and <= '9' => value - '0',
		>= 'a' and <= 'f' => value - 'a' + 10,
		>= 'A' and <= 'F' => value - 'A' + 10,
		_ => -1
	};

	private static bool IsNumericSuffix(string suffix) => suffix is "b" or "f16" or "f32" or "f64" or
		"i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64";

	private static void ReadCharacter(string source, int start, ref int position, List<Token> tokens, List<Diagnostic> diagnostics)
	{
		position++;
		var valid = position < source.Length && source[position] == '\\' && position + 1 < source.Length
			? ReadCharacterEscape(source, ref position)
			: position < source.Length && source[position] != '\'' && source[position] is not '\r' and not '\n' &&
				!char.IsSurrogate(source[position]) && AdvanceCharacterCodeUnit(source, ref position);
		if (valid && position < source.Length && source[position] == '\'')
		{
			position++;
			tokens.Add(new(TokenKind.CharLiteral, source[start..position], new(start, position - start)));
			return;
		}
		while (position < source.Length && source[position] is not '\'' and not '\r' and not '\n') position++;
		if (position < source.Length && source[position] == '\'') position++;
		diagnostics.Add(new("LEX004", "Некорректный символьный литерал", new(start, position - start)));
		tokens.Add(new(TokenKind.CharLiteral, source[start..position], new(start, position - start)));
	}

	private static bool AdvanceCharacterCodeUnit(string source, ref int position)
	{
		position++;
		return true;
	}

	private static bool ReadCharacterEscape(string source, ref int position)
	{
		if (source[position + 1] is '0' or 'a' or 'b' or 'f' or 'n' or 'r' or 't' or 'v' or '\\' or '\'' or '"')
		{
			position += 2;
			return true;
		}
		if (source[position + 1] != 'u' || position + 5 >= source.Length)
		{
			position += 2;
			return false;
		}
		for (var index = position + 2; index < position + 6; index++)
			if (DigitValue(source[index]) is < 0 or > 15)
			{
				position += 2;
				return false;
			}
		position += 6;
		return true;
	}

	private static void ReadString(string source, int start, ref int position, List<Token> tokens, List<Diagnostic> diagnostics)
	{
		var prefixEnd = source[position] == '"' ? position : position + 1;
		var raw = source[start..prefixEnd].Contains('r');
		var verbatim = source[start..prefixEnd].Contains('v');
		var interpolated = source[start..prefixEnd].Contains('f');
		var triple = source.AsSpan(prefixEnd).StartsWith("\"\"\"", StringComparison.Ordinal);
		position = triple ? prefixEnd + 3 : prefixEnd + 1;
		while (position < source.Length)
		{
			if (interpolated && source[position] == '$' && position + 1 < source.Length && source[position + 1] == '{')
			{
				position += 2;
				if (!ScanInterpolation(source, ref position))
					break;
				continue;
			}
			if (triple ? source.AsSpan(position).StartsWith("\"\"\"", StringComparison.Ordinal) : source[position] == '"')
			{
				if (verbatim && !triple && position + 1 < source.Length && source[position + 1] == '"')
				{
					position += 2;
					continue;
				}
				position += triple ? 3 : 1;
				tokens.Add(new(TokenKind.StringLiteral, source[start..position], new(start, position - start)));
				return;
			}
			if (!raw && source[position] == '\\' && position + 1 < source.Length) position += 2;
			else position++;
		}
		diagnostics.Add(new("LEX003", "Незакрытый строковый литерал", new(start, source.Length - start)));
		tokens.Add(new(TokenKind.StringLiteral, source[start..position], new(start, position - start)));
	}

	/// <summary>
	/// Найти закрывающую <c>}</c> интерполяции, начиная сразу после <c>${</c>.
	/// Возвращает индекс <c>}</c> в <paramref name="source"/> или -1.
	/// </summary>
	public static int FindInterpolationEnd(string source, int start)
	{
		ArgumentNullException.ThrowIfNull(source);
		var position = start;
		return ScanInterpolation(source, ref position) ? position - 1 : -1;
	}

	/// <summary>Пропустить вложенный строковый литерал (нужно разбору интерполяции в парсере).</summary>
	internal static bool TrySkipString(string source, ref int position) =>
		TrySkipNestedString(source, ref position);

	private static bool ScanInterpolation(string source, ref int position)
	{
		var depth = 1;
		while (position < source.Length)
		{
			if (source[position] == '/' && position + 1 < source.Length && source[position + 1] == '/')
			{
				position += 2;
				while (position < source.Length && source[position] is not '\r' and not '\n') position++;
				continue;
			}
			if (source.AsSpan(position).StartsWith("/*", StringComparison.Ordinal))
			{
				position += 2;
				var commentDepth = 1;
				while (position + 1 < source.Length && commentDepth > 0)
				{
					if (source.AsSpan(position).StartsWith("/*", StringComparison.Ordinal))
					{
						commentDepth++;
						position += 2;
					}
					else if (source.AsSpan(position).StartsWith("*/", StringComparison.Ordinal))
					{
						commentDepth--;
						position += 2;
					}
					else
						position++;
				}
				if (commentDepth > 0) return false;
				continue;
			}
			if (TrySkipNestedString(source, ref position))
				continue;
			if (source[position] == '{') depth++;
			else if (source[position] == '}' && --depth == 0)
			{
				position++;
				return true;
			}
			position++;
		}
		return false;
	}

	private static bool TrySkipNestedString(string source, ref int position)
	{
		var quote = position;
		if (source[quote] != '"')
		{
			var matchedPrefix = false;
			foreach (var candidate in StringPrefixes)
			{
				if (!source.AsSpan(position).StartsWith(candidate, StringComparison.Ordinal) ||
					position + candidate.Length >= source.Length || source[position + candidate.Length] != '"')
					continue;
				quote += candidate.Length;
				matchedPrefix = true;
				break;
			}
			if (!matchedPrefix) return false;
		}
		else
		{
			var prefixStart = position;
			while (prefixStart > 0 && char.IsLetter(source[prefixStart - 1])) prefixStart--;
			if (StringPrefixes.Contains(source[prefixStart..position]))
				quote = position;
		}
		var prefix = quote == position ? string.Empty : source[position..quote];
		var triple = source.AsSpan(quote).StartsWith("\"\"\"", StringComparison.Ordinal);
		var raw = prefix.Contains('r');
		position = quote + (triple ? 3 : 1);
		while (position < source.Length)
		{
			if (triple ? source.AsSpan(position).StartsWith("\"\"\"", StringComparison.Ordinal) : source[position] == '"')
			{
				position += triple ? 3 : 1;
				return true;
			}
			if (!raw && source[position] == '\\' && position + 1 < source.Length) position += 2;
			else position++;
		}
		return true;
	}
}
