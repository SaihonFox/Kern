using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed partial class Parser
{
	private MatchExpression ParseMatchExpression()
	{
		var start = Consume(TokenKind.Match, "Ожидалось ключевое слово match").Span.Start;
		var hasTarget = Current.Kind != TokenKind.LeftBrace;
		var target = hasTarget ? ParseExpression() : new IdentifierExpression(string.Empty, Current.Span);
		Consume(TokenKind.LeftBrace, "Ожидалась открывающая фигурная скобка match");
		var arms = new List<MatchArm>();
		while (!AtEnd && Current.Kind != TokenKind.RightBrace)
		{
			var before = _position;
			var pattern = hasTarget ? ParseOrPattern() : ParsePureGuardPattern();
			ExpressionNode? guard = null;
			if (Current.Kind == TokenKind.Identifier && Current.Text == "if")
			{
				Advance();
				guard = ParseExpression();
			}
			else if (Match(TokenKind.Colon))
				guard = ParseExpression();
			if (Current.Kind is TokenKind.Operator && Current.Text == "=>")
				Advance();
			else
				Report("PAR005", "Ожидался оператор => после паттерна");
			ExpressionNode body;
			if (Current.Kind == TokenKind.LeftBrace)
			{
				var block = ParseBlock();
				body = new BlockExpression(block, block.Span);
			}
			else
				body = ParseExpression();
			var armStart = pattern.Span.Start;
			arms.Add(new MatchArm(pattern, body, new TextSpan(armStart, body.Span.End - armStart), guard));
			Match(TokenKind.Semicolon);
			if (_position == before)
				Advance();
			if (Current.Kind is not (TokenKind.RightBrace or TokenKind.Semicolon) && _position == before + 1)
				RecoverMatchArm();
		}
		var close = Consume(TokenKind.RightBrace, "Ожидалась закрывающая фигурная скобка match");
		return new MatchExpression(target, arms, new TextSpan(start, close.Span.End - start), !hasTarget);
	}

	/// <summary>
	/// B4a2: or-паттерн <c>p1 | p2</c>. <c>|</c> читается только в позиции паттерна ветви,
	/// поэтому выражения guard и тела по-прежнему трактуют <c>|</c> как битовое ИЛИ.
	/// </summary>
	private PatternNode ParseOrPattern()
	{
		var first = ParsePattern();
		if (Current.Kind != TokenKind.Operator || Current.Text != "|")
			return first;

		var alternatives = new List<PatternNode> { first };
		while (Current.Kind == TokenKind.Operator && Current.Text == "|")
		{
			Advance();
			alternatives.Add(ParsePattern());
		}

		return new OrPattern(alternatives, new TextSpan(first.Span.Start, alternatives[^1].Span.End - first.Span.Start));
	}

	/// <summary>
	/// Отличает структурный паттерн <c>Point { x: var a }</c> от блока после <c>is Type</c>:
	/// внутри фигурных скобок ожидается <c>имя :</c> или <c>..</c>.
	/// </summary>
	private bool IsStructuralBracePatternAhead()
	{
		if (Current.Kind != TokenKind.LeftBrace || _position + 1 >= _tokens.Count)
			return false;
		Token next = _tokens[_position + 1];
		if (next.Kind == TokenKind.Operator && next.Text == "..")
			return true;
		return next.Kind == TokenKind.Identifier &&
			_position + 2 < _tokens.Count &&
			_tokens[_position + 2].Kind == TokenKind.Colon;
	}

	private PatternNode ParsePattern()
	{
		var token = Current;
		if (token.Kind == TokenKind.Var)
		{
			Advance();
			var name = Consume(TokenKind.Identifier, "Ожидалось имя привязки после var");
			return new BindingPattern(
				name.Text,
				true,
				new TextSpan(token.Span.Start, name.Span.End - token.Span.Start));
		}
		if (token.Kind == TokenKind.Operator && token.Text == "..")
		{
			Advance();
			string? name = null;
			if (Current.Kind == TokenKind.Identifier && Current.Text != "_")
				name = Advance().Text;
			return new RestPattern(name, new TextSpan(token.Span.Start, Previous.Span.End - token.Span.Start));
		}
		if (token.Kind == TokenKind.LeftBracket)
		{
			// B4b1: `[p1, p2]` — паттерн списка фиксированной длины. `..rest` разбирается
			// как RestPattern; binder сообщит KRN2114 (B4b2).
			Advance();
			var items = new List<PatternNode>();
			while (!AtEnd && Current.Kind != TokenKind.RightBracket)
			{
				var before = _position;
				items.Add(ParsePattern());
				if (!Match(TokenKind.Comma)) break;
				if (_position == before) Advance();
			}
			var close = Consume(TokenKind.RightBracket, "Ожидалась закрывающая квадратная скобка паттерна списка");
			return new ListPattern(items, new TextSpan(token.Span.Start, close.Span.End - token.Span.Start));
		}
		if (token.Kind == TokenKind.Identifier)
		{
			Advance();
			if (Current.Kind == TokenKind.LeftParen && IsStructuralPatternAhead())
				return ParseNamedStructuralPattern(token);
			if (Current.Kind == TokenKind.LeftParen)
			{
				Advance();
				var arguments = new List<PatternNode>();
				while (!AtEnd && Current.Kind != TokenKind.RightParen)
				{
					var before = _position;
					arguments.Add(ParsePattern());
					if (!Match(TokenKind.Comma)) break;
					if (_position == before) Advance();
				}
				var close = Consume(TokenKind.RightParen, "Ожидалась закрывающая скобка паттерна");
				return new ConstructorPattern(token.Text, arguments, new TextSpan(token.Span.Start, close.Span.End - token.Span.Start));
			}
			if (Current.Kind == TokenKind.LeftBrace && IsStructuralBracePatternAhead())
				return ParseNamedStructuralPattern(token);
			// Generic/Nullable-тип в позиции паттерна: `is list<i32>`, `is str?`.
			if (Current.Kind == TokenKind.Operator && Current.Text is "<" or "?")
			{
				var start = _position;
				var diagnosticCount = _diagnostics.Count;
				var type = ParseTypeSyntaxFrom(token);
				if (type is NamedTypeSyntax)
					return new TypePattern(type, "", new TextSpan(token.Span.Start, type.Span.End - token.Span.Start));
				_position = start;
				DiscardDiagnosticsSince(diagnosticCount);
			}
			if (Current.Kind == TokenKind.Identifier && Current.Text != "if")
			{
				var name = Advance();
				return new TypePattern(NamedTypeSyntax.Plain(token.Text, token.Span), name.Text, new TextSpan(token.Span.Start, name.Span.End - token.Span.Start));
			}
			return token.Text == "_" ? new WildcardPattern(token.Span) : new IdentifierPattern(token.Text, token.Span);
		}
		if (token.Kind is TokenKind.IntegerLiteral or TokenKind.NumberLiteral or TokenKind.StringLiteral or TokenKind.CharLiteral)
		{
			Advance();
			var startPattern = new LiteralPattern(token.Text, token.Kind, token.Span);
			if (Current.Kind == TokenKind.Operator && Current.Text is ".." or "..=")
			{
				var range = Advance();
				var endToken = Current;
				PatternNode endPattern;
				if (endToken.Kind is TokenKind.IntegerLiteral or TokenKind.NumberLiteral or TokenKind.StringLiteral or TokenKind.CharLiteral)
				{
					Advance();
					endPattern = new LiteralPattern(endToken.Text, endToken.Kind, endToken.Span);
				}
				else if (endToken.Kind is TokenKind.Identifier or TokenKind.LeftParen)
				{
					// Не литерал (имя или выражение): синтаксис сохраняем, binder сообщит KRN2114.
					var expression = ParseExpression();
					endPattern = new ExpressionPattern(expression, expression.Span);
				}
				else
				{
					Report("PAR006", "Ожидалась граница диапазона в паттерне");
					endPattern = new LiteralPattern(endToken.Text, endToken.Kind, endToken.Span);
				}
				return new RangePattern(startPattern, endPattern, range.Text == "..=",
					new TextSpan(token.Span.Start, endPattern.Span.End - token.Span.Start));
			}
			return startPattern;
		}
		Report("PAR006", "Ожидался паттерн match");
		if (!AtEnd && Current.Kind is not (TokenKind.RightBrace or TokenKind.RightParen or TokenKind.Comma or TokenKind.Semicolon or TokenKind.Operator))
			Advance();
		return new WildcardPattern(token.Span);
	}

	private StructuralPattern ParseNamedStructuralPattern(Token type)
	{
		var closeKind = Current.Kind == TokenKind.LeftParen ? TokenKind.RightParen : TokenKind.RightBrace;
		Advance();
		var fields = new List<NamedFieldPattern>();
		string? restName = null;
		var restCount = 0;
		while (!AtEnd && Current.Kind != closeKind)
		{
			if (Current.Kind == TokenKind.Operator && Current.Text == "..")
			{
				Advance();
				restCount++;
				if (Current.Kind == TokenKind.Identifier && Current.Text != "_")
					restName = Advance().Text;
			}
			else if (Current.Kind == TokenKind.Identifier)
			{
				var name = Advance();
				PatternNode? nested = null;
				if (Match(TokenKind.Colon))
				{
					if (Current.Kind == TokenKind.Var)
					{
						var variable = Advance();
					var binding = Consume(TokenKind.Identifier, "Ожидалось имя связанной переменной");
					nested = new BindingPattern(binding.Text, true,
						new TextSpan(variable.Span.Start, binding.Span.End - variable.Span.Start));
					}
					else
						nested = ParsePattern();
				}
				else
					nested = new IdentifierPattern(name.Text, name.Span);
				fields.Add(new NamedFieldPattern(name.Text, nested,
					new TextSpan(name.Span.Start, (nested?.Span.End ?? name.Span.End) - name.Span.Start)));
			}
			else
			{
				Report("PAR006", "Ожидался именованный член structural pattern");
				Advance();
			}
			if (!Match(TokenKind.Comma)) break;
		}
		var close = Consume(closeKind, "Ожидалась закрывающая скобка structural pattern");
		return new StructuralPattern(NamedTypeSyntax.Plain(type.Text, type.Span), fields, restCount > 0, new TextSpan(type.Span.Start, close.Span.End - type.Span.Start), restName, restCount);
	}

	private bool IsStructuralPatternAhead()
	{
		var fieldIndex = _position + 1;
		if (fieldIndex >= _tokens.Count) return false;
		if (_tokens[fieldIndex].Kind == TokenKind.Operator && _tokens[fieldIndex].Text == "..") return true;
		return _tokens[fieldIndex].Kind == TokenKind.Identifier && fieldIndex + 1 < _tokens.Count &&
			_tokens[fieldIndex + 1].Kind == TokenKind.Colon;
	}

	private PatternNode ParsePureGuardPattern()
	{
		var expression = ParseExpression();
		if (Current.Kind is TokenKind.Operator && Current.Text == "=>")
			return new ExpressionPattern(expression, expression.Span);
		if (Current.Kind == TokenKind.Identifier && Current.Text == "if")
			return new ExpressionPattern(expression, expression.Span);
		return expression is IdentifierExpression identifier && identifier.Name == "_"
			? new WildcardPattern(expression.Span)
			: new ExpressionPattern(expression, expression.Span);
	}

	private void RecoverMatchArm()
	{
		while (!AtEnd && Current.Kind is not (TokenKind.Semicolon or TokenKind.RightBrace))
			Advance();
		Match(TokenKind.Semicolon);
	}

}
