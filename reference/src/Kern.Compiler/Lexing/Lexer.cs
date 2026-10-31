using System.Globalization;
using System.Numerics;
using System.Text;
using Kern.Core;

namespace Kern.Compiler.Lexing;

/// <summary>
/// Лексер Kern. Переводы строк выдаются как токен NewLine (подряд идущие схлопываются),
/// значимость решает парсер. Интерполированные строки f"..." — один токен FString с частями;
/// вложенные выражения лексятся рекурсивно (абсолютные позиции сохраняются).
/// </summary>
public sealed class Lexer
{
	private readonly SourceFile _file;
	private readonly DiagnosticBag _diag;
	private readonly string _src;
	private readonly int _from;
	private readonly int _end;
	private int _pos;
	private readonly List<Token> _tokens = [];

	private Lexer(SourceFile file, DiagnosticBag diag, int from, int end)
	{
		_file = file;
		_diag = diag;
		_src = file.Text;
		_from = from;
		_end = end;
		_pos = from;
	}

	/// <summary>Токенизирует файл целиком. Последний токен — Eof.</summary>
	public static List<Token> Lex(SourceFile file, DiagnosticBag diagnostics) =>
		new Lexer(file, diagnostics, 0, file.Text.Length).Run();

	private List<Token> Run()
	{
		while (true)
		{
			SkipTrivia();
			if (_pos >= _end) break;
			LexToken();
		}
		_tokens.Add(new Token(TokenKind.Eof, "", new TextSpan(_end, 0)));
		return _tokens;
	}

	// ---------- Вспомогательное ----------

	private char Peek(int offset = 0) => _pos + offset < _end ? _src[_pos + offset] : '\0';

	private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

	private static bool IsIdentPart(char c) =>
		char.IsLetterOrDigit(c) || c == '_' ||
		char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

	private static bool IsDigitOf(int radix, char c) => radix switch
	{
		2 => c is '0' or '1',
		8 => c is >= '0' and <= '7',
		16 => char.IsAsciiHexDigit(c),
		_ => char.IsAsciiDigit(c),
	};

	private void Error(string code, string message, int start, int length) =>
		_diag.Error(code, message, _file, new TextSpan(start, Math.Max(length, 0)));

	private void Add(TokenKind kind, int start, object? value = null) =>
		_tokens.Add(new Token(kind, _src[start.._pos], TextSpan.FromBounds(start, _pos), value));

	private void Emit(TokenKind kind, int length)
	{
		int start = _pos;
		_pos += length;
		Add(kind, start);
	}

	// ---------- Пробелы и комментарии ----------

	private void SkipTrivia()
	{
		while (_pos < _end)
		{
			char c = _src[_pos];
			if (c is ' ' or '\t' or '\r' or '\f' or '\v' || (c > 127 && char.IsWhiteSpace(c)))
			{
				_pos++;
			}
			else if (c == '\n')
			{
				// Схлопываем подряд идущие переводы строк в один токен
				if (_tokens.Count > 0 && _tokens[^1].Kind != TokenKind.NewLine)
					_tokens.Add(new Token(TokenKind.NewLine, "\n", new TextSpan(_pos, 1)));
				_pos++;
			}
			else if (c == '/' && Peek(1) == '/')
			{
				while (_pos < _end && _src[_pos] != '\n') _pos++;
			}
			else if (c == '/' && Peek(1) == '*')
			{
				int start = _pos;
				_pos += 2;
				bool closed = false;
				while (_pos < _end)
				{
					if (_src[_pos] == '*' && Peek(1) == '/') { _pos += 2; closed = true; break; }
					_pos++;
				}
				if (!closed) Error("LEX001", "Незавершённый многострочный комментарий", start, 2);
			}
			else break;
		}
	}

	// ---------- Основной разбор токена ----------

	private void LexToken()
	{
		char c = Peek();
		int start = _pos;

		if (TryStringPrefix(out int prefixLen, out var mode, out bool interp))
		{
			_pos += prefixLen;
			LexString(start, interp, mode);
		}
		else if (IsIdentStart(c)) LexIdent();
		else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(Peek(1)) && !AfterOperand(start))) LexNumber();
		else if (c == '"') LexString(start, interpolated: false);
		else if (c == '\'') LexChar();
		else LexPunct();
	}

	/// <summary>
	/// Префиксы строк: `f` (интерполяция), `r`/`fr` (raw: без escape; `"""…"""` — многострочная),
	/// `v`/`fv` (verbatim: без escape, `""` = кавычка, многострочная), `x`/`fx` (regex: как raw, тип regex).
	/// </summary>
	private bool TryStringPrefix(out int len, out StrMode mode, out bool interp)
	{
		len = 0; mode = StrMode.Escaped; interp = false;
		if (_pos > _from && IsIdentPart(_src[_pos - 1])) return false;
		foreach (var (text, m, i) in StringPrefixes)
		{
			if (string.CompareOrdinal(_src, _pos, text, 0, text.Length) != 0) continue;
			if (_pos + text.Length >= _end || _src[_pos + text.Length] != '"') continue;
			len = text.Length; mode = m; interp = i;
			return true;
		}
		return false;
	}

	private static readonly (string Text, StrMode Mode, bool Interp)[] StringPrefixes =
	[
		("fx", StrMode.Raw, true), ("fr", StrMode.Raw, true), ("fv", StrMode.Verbatim, true),
		("x", StrMode.Raw, false), ("r", StrMode.Raw, false), ("v", StrMode.Verbatim, false),
		("f", StrMode.Escaped, true),
	];

	private enum StrMode { Escaped, Raw, Verbatim }

	/// <summary>Предыдущий символ вплотную — идентификатор/`)`/`]`: тогда `.5` это доступ к члену, а не число.</summary>
	private bool AfterOperand(int start) =>
		start > _from && (IsIdentPart(_src[start - 1]) || _src[start - 1] is ')' or ']');

	private void LexIdent()
	{
		int start = _pos;
		while (_pos < _end && IsIdentPart(_src[_pos])) _pos++;
		string text = _src[start.._pos];
		if (text == "_") Add(TokenKind.Underscore, start);
		else if (TokenFacts.Keywords.TryGetValue(text, out var kw)) Add(kw, start);
		else Add(TokenKind.Ident, start);
	}

	// ---------- Числа ----------

	private void LexNumber()
	{
		int start = _pos;
		int radix = 10;
		if (Peek() == '0')
		{
			char p = char.ToLowerInvariant(Peek(1));
			if (p == 'x' && char.IsAsciiHexDigit(Peek(2))) radix = 16;
			else if (p == 'b' && Peek(2) is '0' or '1') radix = 2;
			else if (p == 'h' && Peek(2) is >= '0' and <= '7') radix = 8;
		}

		var digits = new StringBuilder();
		bool isFloat = false;

		if (radix != 10)
		{
			_pos += 2;
			ReadDigits(radix, digits);
			if (char.IsAsciiDigit(Peek()))
				Error("LEX010", $"Недопустимая цифра '{Peek()}' в числе по основанию {radix}", _pos, 1);
		}
		else
		{
			if (Peek() != '.') ReadDigits(10, digits);
			if (Peek() == '.' && char.IsAsciiDigit(Peek(1)))
			{
				isFloat = true;
				digits.Append('.');
				_pos++;
				ReadDigits(10, digits);
			}
			if (Peek() is 'e' or 'E' &&
				(char.IsAsciiDigit(Peek(1)) || (Peek(1) is '+' or '-' && char.IsAsciiDigit(Peek(2)))))
			{
				isFloat = true;
				digits.Append('e');
				_pos++;
				if (Peek() is '+' or '-') digits.Append(_src[_pos++]);
				ReadDigits(10, digits);
			}
		}

		// Суффикс точного типа
		string? suffix = null;
		if (IsIdentStart(Peek()) && Peek() != '_')
		{
			int sStart = _pos;
			while (_pos < _end && IsIdentPart(_src[_pos])) _pos++;
			string raw = _src[sStart.._pos];
			if (TokenFacts.NumberSuffixes.Contains(raw)) suffix = TokenFacts.NormalizeSuffix(raw);
			else Error("LEX011", $"Недопустимый суффикс числового литерала '{raw}'", sStart, raw.Length);
		}

		BigInteger integer = BigInteger.Zero;
		double dbl;
		if (isFloat)
		{
			dbl = double.Parse(digits.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
			if (suffix is not null && !suffix.StartsWith('f'))
				Error("LEX012", $"Суффикс '{suffix}' нельзя применить к дробному литералу", start, _pos - start);
		}
		else
		{
			foreach (char ch in digits.ToString())
				integer = integer * radix + (char.IsAsciiDigit(ch) ? ch - '0' : char.ToLowerInvariant(ch) - 'a' + 10);
			dbl = (double)integer;
			if (suffix is not null && suffix.StartsWith('f')) isFloat = true;
		}

		Add(TokenKind.Number, start, new NumberValue(integer, dbl, isFloat, suffix));
	}

	/// <summary>Читает цифры с разделителями `_` (только между цифрами).</summary>
	private void ReadDigits(int radix, StringBuilder sb)
	{
		while (_pos < _end)
		{
			char c = _src[_pos];
			if (IsDigitOf(radix, c)) { sb.Append(c); _pos++; }
			else if (c == '_' && IsDigitOf(radix, Peek(1)) && sb.Length > 0) _pos++;
			else break;
		}
	}

	// ---------- Строки и символы ----------

	private void LexString(int start, bool interpolated, StrMode mode = StrMode.Escaped)
	{
		_pos++; // открывающая "
		bool triple = false;
		if (mode == StrMode.Raw && Peek() == '"' && Peek(1) == '"') { triple = true; _pos += 2; }
		var sb = new StringBuilder();
		var parts = new List<FStringPart>();
		int litStart = _pos;
		bool closed = false;

		void FlushLiteral()
		{
			if (sb.Length == 0) return;
			parts.Add(new FStringText(sb.ToString(), TextSpan.FromBounds(litStart, _pos)));
			sb.Clear();
		}

		while (_pos < _end)
		{
			char c = _src[_pos];
			if (c == '"')
			{
				if (mode == StrMode.Verbatim && Peek(1) == '"') { sb.Append('"'); _pos += 2; continue; }
				if (triple)
				{
					if (Peek(1) == '"' && Peek(2) == '"') { _pos += 3; closed = true; break; }
					sb.Append('"'); _pos++; continue;
				}
				_pos++; closed = true; break;
			}
			if (c == '\\' && mode == StrMode.Escaped) { ReadEscape(sb); continue; }
			if (interpolated && c == '$' && Peek(1) == '{')
			{
				FlushLiteral();
				LexInterpolation(parts);
				litStart = _pos;
				continue;
			}
			sb.Append(c);
			_pos++;
		}

		if (!closed) Error("LEX002", "Незавершённая строка", start, 1);
		if (interpolated)
		{
			FlushLiteral();
			Add(TokenKind.FString, start, parts);
		}
		else Add(TokenKind.String, start, sb.ToString());
	}

	private void LexChar()
	{
		int start = _pos;
		_pos++; // открывающая '
		var sb = new StringBuilder();
		if (Peek() == '\\') ReadEscape(sb);
		else if (_pos < _end && _src[_pos] is not ('\'' or '\n'))
		{
			sb.Append(_src[_pos++]);
			if (char.IsHighSurrogate(sb[0]) && _pos < _end && char.IsLowSurrogate(_src[_pos])) sb.Append(_src[_pos++]);
		}
		else Error("LEX003", "Пустой символьный литерал", start, 2);

		if (Peek() == '\'') _pos++;
		else
		{
			Error("LEX004", "Символьный литерал должен содержать ровно один символ и закрываться '", start, _pos - start);
			while (_pos < _end && _src[_pos] is not ('\'' or '\n')) _pos++;
			if (Peek() == '\'') _pos++;
		}
		int cp = sb.Length > 0 ? char.ConvertToUtf32(sb.ToString(), 0) : 0;
		Add(TokenKind.Char, start, cp);
	}

	/// <summary>Читает escape-последовательность; _pos стоит на '\'.</summary>
	private void ReadEscape(StringBuilder sb)
	{
		int start = _pos;
		_pos++;
		char n = Peek();
		if (_pos >= _end) { Error("LEX005", "Незавершённая escape-последовательность", start, 1); return; }
		_pos++;
		switch (n)
		{
			case 'n': sb.Append('\n'); break;
			case 't': sb.Append('\t'); break;
			case 'r': sb.Append('\r'); break;
			case '0': sb.Append('\0'); break;
			case '\\': sb.Append('\\'); break;
			case '"': sb.Append('"'); break;
			case '\'': sb.Append('\''); break;
			case '$': sb.Append('$'); break;
			case 'x':
			{
				int v = ReadHex(2, out int count);
				if (count != 2) Error("LEX006", "Ожидалось две hex-цифры после \\x", start, _pos - start);
				else sb.Append((char)v);
				break;
			}
			case 'u':
			{
				if (Peek() != '{') { Error("LEX006", "Ожидалось \\u{XXXX}", start, _pos - start); break; }
				_pos++;
				int v = ReadHex(6, out int count);
				if (Peek() == '}') _pos++;
				else Error("LEX006", "Ожидалась '}' в \\u{...}", start, _pos - start);
				if (count == 0 || v > 0x10FFFF || v is >= 0xD800 and <= 0xDFFF)
					Error("LEX006", "Недопустимая кодовая точка в \\u{...}", start, _pos - start);
				else sb.Append(char.ConvertFromUtf32(v));
				break;
			}
			default:
				Error("LEX007", $"Неизвестная escape-последовательность '\\{n}'", start, 2);
				sb.Append(n);
				break;
		}
	}

	private int ReadHex(int max, out int count)
	{
		int v = 0;
		count = 0;
		while (count < max && char.IsAsciiHexDigit(Peek()))
		{
			v = v * 16 + Convert.ToInt32(Peek().ToString(), 16);
			_pos++;
			count++;
		}
		return v;
	}

	// ---------- Интерполяция ----------

	/// <summary>Разбирает `${expr}`, `${expr=}`, `${expr:fmt}`; _pos стоит на '$'.</summary>
	private void LexInterpolation(List<FStringPart> parts)
	{
		int partStart = _pos;
		int exprStart = _pos + 2;
		int i = exprStart, depth = 0, pendingQ = 0;
		int exprEnd = -1, fmtStart = -1;

		while (i < _end)
		{
			char ch = _src[i];
			if (ch == '"') { i = SkipQuoted(i, '"'); continue; }
			if (ch == '\'') { i = SkipQuoted(i, '\''); continue; }
			if (ch == '}' && depth == 0) { exprEnd = i; break; }
			if (ch is '(' or '[' or '{') depth++;
			else if (ch is ')' or ']' or '}') depth--;
			else if (ch == '?' && depth == 0)
			{
				char nx = i + 1 < _end ? _src[i + 1] : '\0';
				if (nx is '.' or ':') { i += 2; continue; } // `?.` и `?:` — не тернарный
				pendingQ++;
			}
			else if (ch == ':' && depth == 0)
			{
				if (i + 1 < _end && _src[i + 1] == ':') { i += 2; continue; }
				if (pendingQ > 0) pendingQ--;
				else { exprEnd = i; fmtStart = i + 1; break; }
			}
			i++;
		}

		if (exprEnd < 0)
		{
			Error("LEX008", "Незавершённая интерполяция ${...}", partStart, 2);
			_pos = _end;
			return;
		}

		string? format = null;
		int after = exprEnd + 1;
		if (fmtStart >= 0)
		{
			int close = _src.IndexOf('}', fmtStart, _end - fmtStart);
			if (close < 0)
			{
				Error("LEX008", "Незавершённая интерполяция ${...}", partStart, 2);
				_pos = _end;
				return;
			}
			format = _src[fmtStart..close];
			after = close + 1;
		}

		// Форма `expr=`
		int subEnd = exprEnd;
		while (subEnd > exprStart && char.IsWhiteSpace(_src[subEnd - 1])) subEnd--;
		bool debug = false;
		if (subEnd > exprStart && _src[subEnd - 1] == '=' &&
			!(subEnd - 2 >= exprStart && _src[subEnd - 2] is '=' or '!' or '<' or '>'))
		{
			debug = true;
			subEnd--;
		}

		if (_src[exprStart..subEnd].Trim().Length == 0)
			Error("LEX009", "Пустое выражение в интерполяции", partStart, after - partStart);

		var inner = new Lexer(_file, _diag, exprStart, subEnd).Run();
		parts.Add(new FStringExpr(inner, _src[exprStart..subEnd].Trim(), debug, format,
			TextSpan.FromBounds(partStart, after)));
		_pos = after;
	}

	/// <summary>Пропускает строковый/символьный литерал внутри интерполяции; возвращает позицию после него.</summary>
	private int SkipQuoted(int i, char quote)
	{
		i++;
		while (i < _end && _src[i] != quote)
		{
			if (_src[i] == '\\') i++;
			i++;
		}
		return Math.Min(i + 1, _end);
	}

	// ---------- Пунктуация и операторы ----------

	private void LexPunct()
	{
		char c = Peek(), n = Peek(1);

		void One(TokenKind k) => Emit(k, 1);
		void Pair(char second, TokenKind two, TokenKind one) => Emit(n == second ? two : one, n == second ? 2 : 1);

		switch (c)
		{
			case '+': Pair('=', TokenKind.PlusEq, TokenKind.Plus); break;
			case '*':
				if (n == '*') Emit(Peek(2) == '=' ? TokenKind.StarStarEq : TokenKind.StarStar, Peek(2) == '=' ? 3 : 2);
				else Pair('=', TokenKind.StarEq, TokenKind.Star);
				break;
			case '/': Pair('=', TokenKind.SlashEq, TokenKind.Slash); break;
			case '%': Pair('=', TokenKind.PercentEq, TokenKind.Percent); break;
			case '^': Pair('=', TokenKind.CaretEq, TokenKind.Caret); break;
			case '-':
				if (n == '>') Emit(TokenKind.Arrow, 2);
				else Pair('=', TokenKind.MinusEq, TokenKind.Minus);
				break;
			case '=':
				if (n == '=') Emit(Peek(2) == '=' ? TokenKind.EqEqEq : TokenKind.EqEq, Peek(2) == '=' ? 3 : 2);
				else if (n == '>') Emit(TokenKind.FatArrow, 2);
				else One(TokenKind.Eq);
				break;
			case '!':
				if (n == '=') Emit(Peek(2) == '=' ? TokenKind.BangEqEq : TokenKind.BangEq, Peek(2) == '=' ? 3 : 2);
				else if (n == 'i' && Peek(2) == 'n' && !IsIdentPart(Peek(3))) Emit(TokenKind.NotIn, 3);
				else if (n == 'i' && Peek(2) == 's' && !IsIdentPart(Peek(3))) Emit(TokenKind.NotIs, 3);
				else One(TokenKind.Bang);
				break;
			case '<': Pair('=', TokenKind.LtEq, TokenKind.Lt); break;
			case '>': Pair('=', TokenKind.GtEq, TokenKind.Gt); break;
			case '&':
				if (n == '&') Emit(TokenKind.AmpAmp, 2);
				else ReportBad(c, "Используйте '&&' для логического И");
				break;
			case '|':
				if (n == '|') Emit(TokenKind.PipePipe, 2);
				else ReportBad(c, "Используйте '||' для логического ИЛИ");
				break;
			case '?':
				if (n == '.' && !char.IsAsciiDigit(Peek(2))) Emit(TokenKind.QuestionDot, 2);
				else if (n == ':') Emit(TokenKind.QuestionColon, 2);
				else One(TokenKind.Question);
				break;
			case '.':
				if (n == '.') Emit(Peek(2) == '=' ? TokenKind.DotDotEq : TokenKind.DotDot, Peek(2) == '=' ? 3 : 2);
				else One(TokenKind.Dot);
				break;
			case ':': Pair(':', TokenKind.ColonColon, TokenKind.Colon); break;
			case ';': One(TokenKind.Semicolon); break;
			case ',': One(TokenKind.Comma); break;
			case '(': One(TokenKind.LParen); break;
			case ')': One(TokenKind.RParen); break;
			case '[': One(TokenKind.LBracket); break;
			case ']': One(TokenKind.RBracket); break;
			case '{': One(TokenKind.LBrace); break;
			case '}': One(TokenKind.RBrace); break;
			case '$': Pair('[', TokenKind.DollarBracket, TokenKind.Dollar); break;
			case '#': Pair('[', TokenKind.HashBracket, TokenKind.Hash); break;
			case '@': One(TokenKind.At); break;
			case '~': One(TokenKind.Tilde); break;
			default: ReportBad(c, $"Неожиданный символ '{c}'"); break;
		}
	}

	private void ReportBad(char c, string message)
	{
		int start = _pos;
		Error("LEX100", message, start, 1);
		_pos++;
		Add(TokenKind.Error, start);
	}
}
