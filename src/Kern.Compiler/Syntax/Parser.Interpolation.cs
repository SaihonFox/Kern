using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

/// <summary>
/// Разбор интерполированных строк (spec §3). Текст токена хранит исходник целиком, поэтому
/// выражения внутри <c>${…}</c> лексируются заново по подстроке, а все спаны сдвигаются на
/// абсолютное смещение в файле — диагностики указывают внутрь строки.
/// </summary>
public sealed partial class Parser
{
	private ExpressionNode ParseInterpolatedString(Token token, StringLiteralHeader header)
	{
		var content = token.Text[header.ContentStart..header.ContentEnd];
		var contentOffset = token.Span.Start + header.ContentStart;
		var parts = new List<InterpolationPartNode>();
		var textStart = 0;
		var index = 0;
		while (index < content.Length)
		{
			// В обычной строке `\$` не открывает интерполяцию; `fr` сохраняет слэш как текст.
			if (!header.Raw && content[index] == '\\' && index + 1 < content.Length)
			{
				index += 2;
				continue;
			}

			if (content[index] != '$' || index + 1 >= content.Length || content[index + 1] != '{')
			{
				index++;
				continue;
			}

			AddInterpolationText(parts, content, textStart, index, header, contentOffset);
			var innerStart = index + 2;
			var close = Lexer.FindInterpolationEnd(_source, contentOffset + innerStart);
			if (close < 0)
			{
				Report(
					"PAR020",
					"Незакрытая интерполяция в строке",
					new TextSpan(contentOffset + index, content.Length - index));
				return new StringLiteralExpression(token.Text, token.Span);
			}

			var innerEnd = close - contentOffset;
			parts.Add(ParseInterpolationPart(
				content[innerStart..innerEnd],
				contentOffset + innerStart,
				token));
			index = innerEnd + 1;
			textStart = index;
		}

		AddInterpolationText(parts, content, textStart, content.Length, header, contentOffset);
		return new InterpolatedStringExpression(parts, token.Span) { Regex = header.Regex };
	}

	private void AddInterpolationText(
		List<InterpolationPartNode> parts,
		string content,
		int start,
		int end,
		StringLiteralHeader header,
		int contentOffset)
	{
		if (end <= start)
			return;

		if (!StringLiteralText.TryDecodeInterpolationText(
			content[start..end], header.Raw, header.Verbatim, out var text, out var error))
		{
			Report(
				"PAR020",
				error ?? "некорректная escape-последовательность в интерполяции",
				new TextSpan(contentOffset + start, end - start));
			return;
		}

		if (text.Length > 0)
			parts.Add(new InterpolationTextPart(text, new TextSpan(contentOffset + start, end - start)));
	}

	private InterpolationPartNode ParseInterpolationPart(string inner, int absoluteStart, Token token)
	{
		var colon = FindSpecColon(inner);
		var expressionText = colon < 0 ? inner : inner[..colon];
		var spec = colon < 0 ? null : inner[(colon + 1)..].Trim();

		var start = 0;
		while (start < expressionText.Length && char.IsWhiteSpace(expressionText[start]))
			start++;
		var end = expressionText.Length;
		while (end > start && char.IsWhiteSpace(expressionText[end - 1]))
			end--;
		var trimmed = expressionText[start..end];

		var debug = false;
		string? debugText = null;
		if (EndsWithDebugMarker(trimmed))
		{
			debug = true;
			trimmed = trimmed[..^1].TrimEnd();
			debugText = trimmed;
		}

		var span = new TextSpan(
			absoluteStart + start,
			Math.Max(trimmed.Length, debug && trimmed.Length == 0 ? 1 : 0));
		if (trimmed.Length == 0)
			return new InterpolationExpressionPart(null, spec, debug, debugText, span);

		var expression = ParseSubExpression(absoluteStart + start, trimmed.Length);
		return new InterpolationExpressionPart(expression, spec, debug, debugText, span);
	}

	/// <summary>
	/// <c>${expr=}</c>: <c>=</c> в конце, не являющийся частью <c>==</c>, <c>!=</c>, <c>&lt;=</c>,
	/// составного присваивания или диапазона <c>..=</c>.
	/// </summary>
	private static bool EndsWithDebugMarker(string text)
	{
		if (text.Length == 0 || text[^1] != '=')
			return false;
		if (text.Length == 1)
			return true;
		return text[^2] is not ('=' or '!' or '<' or '>' or '+' or '-' or '*' or '/' or '%' or
			'&' or '|' or '^' or '~' or '?' or '.');
	}

	/// <summary>
	/// Первое двоеточие верхнего уровня (вне скобок, строк и комментариев) отделяет текст
	/// выражения от спецификации формата; <c>?:</c> (Elvis) двоеточием не считается.
	/// </summary>
	private static int FindSpecColon(string text)
	{
		var parens = 0;
		var brackets = 0;
		var braces = 0;
		var index = 0;
		while (index < text.Length)
		{
			var character = text[index];
			if (character == '/' && index + 1 < text.Length && text[index + 1] == '/')
			{
				index += 2;
				while (index < text.Length && text[index] is not '\r' and not '\n')
					index++;
				continue;
			}

			if (character == '/' && index + 1 < text.Length && text[index + 1] == '*')
			{
				index += 2;
				var depth = 1;
				while (index + 1 < text.Length && depth > 0)
				{
					if (text[index] == '/' && text[index + 1] == '*')
					{
						depth++;
						index += 2;
					}
					else if (text[index] == '*' && text[index + 1] == '/')
					{
						depth--;
						index += 2;
					}
					else
						index++;
				}
				continue;
			}

			if (character == '"' || char.IsLetter(character))
			{
				var position = index;
				if (Lexer.TrySkipString(text, ref position))
				{
					index = position;
					continue;
				}
			}

			switch (character)
			{
				case '(': parens++; break;
				case ')': parens--; break;
				case '[': brackets++; break;
				case ']': brackets--; break;
				case '{': braces++; break;
				case '}': braces--; break;
				case ':':
					if (parens == 0 && brackets == 0 && braces == 0 &&
						(index == 0 || text[index - 1] != '?'))
						return index;
					break;
			}

			index++;
		}

		return -1;
	}

	/// <summary>Разобрать выражение интерполяции по исходнику с абсолютными смещениями.</summary>
	private ExpressionNode ParseSubExpression(int absoluteStart, int length)
	{
		var text = _source.Substring(absoluteStart, length);
		var lexed = Lexer.Lex(text);
		var tokens = new List<Token>(lexed.Tokens.Count);
		foreach (var token in lexed.Tokens)
			tokens.Add(new Token(token.Kind, token.Text,
				new TextSpan(token.Span.Start + absoluteStart, token.Span.Length)));
		foreach (var diagnostic in lexed.Diagnostics)
			_diagnostics.Add(new Diagnostic(
				diagnostic.Code,
				diagnostic.Message,
				new TextSpan(diagnostic.Span.Start + absoluteStart, diagnostic.Span.Length),
				diagnostic.Severity));

		var parser = new Parser(tokens, _source);
		var expression = parser.ParseExpression();
		if (!parser.AtEnd)
			parser.Report("PAR004", "После выражения в интерполяции ожидалась закрывающая фигурная скобка");
		_diagnostics.AddRange(parser._diagnostics);
		return expression;
	}
}
