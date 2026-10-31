using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed partial class Parser
{
	private ExpressionNode ParseExpression(int minimumPrecedence = 0)
	{
		var left = ParsePrimary();
		while (true)
		{
			if (HasNewlineBeforeCurrent() && Current.Kind != TokenKind.LeadingDot)
				break;
			if (Current.Kind == TokenKind.Operator && Current.Text == "<" && TryParseGenericCall(left, out var genericCall))
			{
				left = genericCall;
				continue;
			}
			if (Current.Kind == TokenKind.Operator && Current.Text == "!")
			{
				Advance();
				if (Current.Kind == TokenKind.LeftParen)
				{
					var macroCall = ParseCall(left);
					if (macroCall is CallExpression call)
						left = call with { IsMacroInvocation = true };
					else
						left = macroCall;
					continue;
				}
			}
			if (Current.Kind == TokenKind.LeftParen)
			{
				left = ParseCall(left);
				continue;
			}
			if (Current.Kind == TokenKind.LeftBracket)
			{
				left = ParseIndex(left);
				continue;
			}
			if (Current.Kind is TokenKind.Dot or TokenKind.LeadingDot || (Current.Kind == TokenKind.Operator && Current.Text == "?."))
			{
				var conditional = Current.Text == "?.";
				Advance();
				var member = ConsumeMemberName("Ожидалось имя члена после точки");
				left = new MemberAccessExpression(left, member.Text, conditional,
					new TextSpan(left.Span.Start, member.Span.End - left.Span.Start));
				continue;
			}
			if (Current.Kind == TokenKind.Operator && (Current.Text == ".." || Current.Text == "..="))
			{
				var rangeToken = Advance();
				var end = ParseExpression(1);
				ExpressionNode? step = null;
				if (Current.Kind == TokenKind.Colon)
				{
					Advance();
					step = ParseExpression(1);
				}
				var rangeEnd = step?.Span.End ?? end.Span.End;
				left = new RangeExpression(left, end, rangeToken.Text == "..=", step,
					new TextSpan(left.Span.Start, rangeEnd - left.Span.Start));
				continue;
			}
			if (!TryGetPrecedence(Current, out var precedence) || precedence < minimumPrecedence)
				break;
			var operatorToken = Advance();
			var right = operatorToken.Text == "?:" && Current.Kind == TokenKind.Ret
				? ParseReturnExpression()
				: ParseExpression(precedence + (operatorToken.Text == "**" ? 0 : 1));
			left = new BinaryExpression(left, operatorToken.Text, right,
				new TextSpan(left.Span.Start, right.Span.End - left.Span.Start));
		}
		return left;
	}

	private UnaryExpression ParseReturnExpression()
	{
		var start = Advance().Span.Start;
		var value = ParseExpression();
		return new UnaryExpression("ret", value, new TextSpan(start, value.Span.End - start));
	}

	private LambdaExpression ParseLambdaExpression()
	{
		var start = Consume(TokenKind.Fun, "Ожидалось ключевое слово fun").Span.Start;
		var parameters = ParseParameters();
		TypeSyntax? returnType = null;
		if (Current.Kind == TokenKind.Operator && Current.Text == "->")
		{
			Advance();
			returnType = ParseTypeSyntax();
		}
		SyntaxNode body;
		if (Current.Kind == TokenKind.LeftBrace)
			body = ParseBlock();
		else
			body = ParseExpression();
		return new LambdaExpression(parameters, returnType, body, new TextSpan(start, body.Span.End - start));
	}

	private IndexExpression ParseIndex(ExpressionNode target)
	{
		Advance();
		var arguments = new List<ExpressionNode>();
		while (!AtEnd && Current.Kind != TokenKind.RightBracket)
		{
			arguments.Add(ParseExpression());
			if (!Match(TokenKind.Comma))
				break;
		}
		var close = Consume(TokenKind.RightBracket, "Ожидалась закрывающая квадратная скобка");
		return new IndexExpression(target, arguments, new TextSpan(target.Span.Start, close.Span.End - target.Span.Start));
	}

	private bool TryParseGenericCall(ExpressionNode target, out ExpressionNode genericCall)
	{
		genericCall = null!;
		var startPosition = _position;
		var diagnosticCount = _diagnostics.Count;
		var typeArguments = new List<TypeSyntax>();
		Advance();
		while (!AtEnd && !IsOperator(">"))
		{
			typeArguments.Add(ParseTypeSyntax());
			if (!Match(TokenKind.Comma))
				break;
			if (IsOperator(">"))
			{
				Report("PAR003", "Ожидался аргумент типа после запятой");
				break;
			}
		}
		if (typeArguments.Count == 0 || !IsOperator(">"))
		{
			// Это не вызов с generic-аргументами (например, сравнение `a < b`): откатываем разбор.
			_position = startPosition;
			DiscardDiagnosticsSince(diagnosticCount);
			return false;
		}
		Advance();
		if (Current.Kind != TokenKind.LeftParen || HasNewlineBeforeCurrent())
		{
			_position = startPosition;
			DiscardDiagnosticsSince(diagnosticCount);
			return false;
		}
		var call = ParseCall(target);
		genericCall = call is StructuredCallExpression structured
			? structured with { TypeArguments = typeArguments }
			: new GenericCallExpression(target, typeArguments,
				((CallExpression)call).Arguments,
				new TextSpan(target.Span.Start, call.Span.End - target.Span.Start));
		return true;
	}

	private ExpressionNode ParseCall(ExpressionNode target)
	{
		Advance();
		var arguments = new List<CallArgumentNode>();
		var hasNamedArguments = false;
		while (!AtEnd && Current.Kind != TokenKind.RightParen)
		{
			var before = _position;
			string? name = null;
			var argumentStart = Current.Span.Start;
			if (Current.Kind == TokenKind.Identifier && _position + 1 < _tokens.Count && _tokens[_position + 1].Kind == TokenKind.Colon)
			{
				name = Advance().Text;
				Advance();
				hasNamedArguments = true;
			}
			if (hasNamedArguments && name is null)
				Report("PAR009", "Позиционный аргумент не может следовать за именованным");
			var value = Current.Kind == TokenKind.Fun
				? ParseLambdaExpression()
				: ParseExpression();
			arguments.Add(new CallArgumentNode(name, value,
				new TextSpan(argumentStart, value.Span.End - argumentStart)));
			if (!Match(TokenKind.Comma))
				break;
			if (_position == before)
				Advance();
		}
		var close = Consume(TokenKind.RightParen, "Ожидалась закрывающая скобка вызова");
		var generic = target as GenericCallExpression;
		var typeArguments = generic?.TypeArguments ?? [];
		var callTarget = generic?.Target ?? target;
		if (hasNamedArguments || typeArguments.Count > 0)
			return new StructuredCallExpression(callTarget, typeArguments, arguments,
				new TextSpan(target.Span.Start, close.Span.End - target.Span.Start));
		return new CallExpression(callTarget, [.. arguments.Select(argument => argument.Value)],
			new TextSpan(target.Span.Start, close.Span.End - target.Span.Start));
	}

	private TypedCollectionExpression ParseTypedCollection()
	{
		var start = Advance().Span.Start;
		var firstType = ParseTypeSyntax();
		TypeSyntax? keyType = null;
		TypeSyntax? valueType = null;
		if (Match(TokenKind.Colon))
		{
			keyType = firstType;
			valueType = ParseTypeSyntax();
		}
		if (Current.Kind == TokenKind.Operator && Current.Text == ">")
			Advance();
		else
			Report("PAR003", "Ожидалась закрывающая угловая скобка");
		Consume(TokenKind.LeftBracket, "Ожидалась открывающая квадратная скобка коллекции");
		var items = new List<ExpressionNode>();
		var entries = new List<CollectionEntryNode>();
		while (!AtEnd && Current.Kind != TokenKind.RightBracket)
		{
			if (Current.Kind == TokenKind.Operator && Current.Text == "..")
			{
				// B3b1: spread допустим и в литерале с явным типом элемента.
				var spread = Advance();
				var spreadExpression = ParseExpression();
				items.Add(new ListSpreadExpression(spreadExpression,
					new TextSpan(spread.Span.Start, spreadExpression.Span.End - spread.Span.Start)));
			}
			else
			{
				var key = ParseExpression();
				if (Match(TokenKind.Colon))
				{
					var value = ParseExpression();
					entries.Add(new CollectionEntryNode(key, value,
						new TextSpan(key.Span.Start, value.Span.End - key.Span.Start)));
				}
				else
					items.Add(key);
			}
			if (!Match(TokenKind.Comma))
				break;
		}
		var close = Consume(TokenKind.RightBracket, "Ожидалась закрывающая квадратная скобка коллекции");
		return new TypedCollectionExpression(keyType is null ? firstType : null, keyType, valueType,
			items, entries, new TextSpan(start, close.Span.End - start));
	}

	private ExpressionNode ParsePrimary()
	{
		if (Current.Kind == TokenKind.Await || Current.Kind == TokenKind.Operator && Current.Text is "!" or "-" or "+" or "~")
		{
			var operatorToken = Advance();
			var operand = ParsePrimary();
			return new UnaryExpression(operatorToken.Text, operand,
				new TextSpan(operatorToken.Span.Start, operand.Span.End - operatorToken.Span.Start));
		}
		if (Current.Kind == TokenKind.Operator && Current.Text == "<")
			return ParseTypedCollection();
		if (Current.Kind == TokenKind.Dollar)
		{
			var dollar = Advance();
			if (Current.Kind == TokenKind.Identifier)
			{
				var member = Advance();
				return new MemberAccessExpression(new SelfExpression(dollar.Span), member.Text, false,
					new TextSpan(dollar.Span.Start, member.Span.End - dollar.Span.Start));
			}
			return new SelfExpression(dollar.Span);
		}
		if (Current.Kind == TokenKind.Identifier && Current.Text == "if")
			return ParseIfExpression(requireElse: true);
		if (Current.Kind == TokenKind.Fun)
			return ParseLambdaExpression();
		if (Current.Kind == TokenKind.Match)
			return ParseMatchExpression();
		if (Current.Kind == TokenKind.Loop || Current.Kind == TokenKind.Async &&
			_position + 1 < _tokens.Count && _tokens[_position + 1].Kind == TokenKind.Loop)
			return ParseLoopExpression();
		if (Current.Kind == TokenKind.Async && _position + 1 < _tokens.Count &&
			_tokens[_position + 1].Kind == TokenKind.Fun)
		{
			Advance();
			return ParseLambdaExpression();
		}
		var token = Current;
		switch (token.Kind)
		{
			case TokenKind.Dollar:
				Advance();
				var self = new SelfExpression(token.Span);
				var member = Consume(TokenKind.Identifier, "После $ ожидалось имя члена");
				return new MemberAccessExpression(self, member.Text, false,
					new TextSpan(token.Span.Start, member.Span.End - token.Span.Start));
			case TokenKind.Identifier:
				Advance();
				return new IdentifierExpression(token.Text, token.Span);
			case TokenKind.IntegerLiteral:
				Advance();
				return new IntegerLiteralExpression(token.Text, token.Span);
			case TokenKind.NumberLiteral:
				Advance();
				return new NumberLiteralExpression(token.Text, token.Span);
			case TokenKind.StringLiteral:
				Advance();
				if (StringLiteralText.TryReadHeader(token.Text, out var header, out _) &&
					header.Interpolated && !header.Regex)
					return ParseInterpolatedString(token, header);
				return new StringLiteralExpression(token.Text, token.Span);
			case TokenKind.CharLiteral:
				Advance();
				return new CharLiteralExpression(token.Text, token.Span);
			case TokenKind.LeftParen:
				Advance();
				var tupleItems = new List<ExpressionNode>();
				var firstItem = ParseExpression();
				tupleItems.Add(firstItem);
				if (Match(TokenKind.Comma))
				{
					while (!AtEnd && Current.Kind != TokenKind.RightParen)
					{
						tupleItems.Add(ParseExpression());
						if (!Match(TokenKind.Comma)) break;
					}
					var tupleClose = Consume(TokenKind.RightParen, "Ожидалась закрывающая скобка кортежа");
					return new TupleExpression(tupleItems, new TextSpan(token.Span.Start, tupleClose.Span.End - token.Span.Start));
				}
				var close = Consume(TokenKind.RightParen, "Ожидалась закрывающая скобка");
				return new ParenthesizedExpression(firstItem,
					new TextSpan(token.Span.Start, close.Span.End - token.Span.Start));
			case TokenKind.LeftBracket:
				Advance();
				var items = new List<ExpressionNode>();
				var entries = new List<CollectionEntryNode>();
				var isDictionary = false;
				while (!AtEnd && Current.Kind != TokenKind.RightBracket)
				{
					if (!isDictionary && Current.Kind == TokenKind.Operator && Current.Text == "..")
					{
						var spread = Advance();
					var spreadExpression = ParseExpression();
					items.Add(new ListSpreadExpression(spreadExpression,
						new TextSpan(spread.Span.Start, spreadExpression.Span.End - spread.Span.Start)));
					}
					else
					{
						var keyOrItem = ParseExpression();
					if (Match(TokenKind.Colon))
					{
						isDictionary = true;
						var value = ParseExpression();
						entries.Add(new CollectionEntryNode(keyOrItem, value,
							new TextSpan(keyOrItem.Span.Start, value.Span.End - keyOrItem.Span.Start)));
					}
					else if (isDictionary)
						Report("PAR006", "Ожидался разделитель : в элементе словаря");
					else
						items.Add(keyOrItem);
					}
					if (!Match(TokenKind.Comma))
						break;
				}
				var bracket = Consume(TokenKind.RightBracket, "Ожидалась закрывающая квадратная скобка");
				return isDictionary
					? new DictionaryExpression(entries, new TextSpan(token.Span.Start, bracket.Span.End - token.Span.Start))
					: new CollectionExpression(items, new TextSpan(token.Span.Start, bracket.Span.End - token.Span.Start));
			default:
				Report("PAR002", "Ожидалось выражение");
				if (!AtEnd) Advance();
				return new IdentifierExpression("<ошибка>", token.Span);
		}
	}

	private bool HasNewlineBeforeCurrent()
	{
		var end = Current.Span.Start;
		var start = Previous.Span.End;
		for (var index = Math.Max(0, start); index < end && index < _source.Length; index++)
			if (_source[index] is '\r' or '\n')
				return true;
		return false;
	}

	private bool TryGetPrecedence(Token token, out int precedence)
	{
		precedence = 0;
		if (token.Kind == TokenKind.In || token.Kind == TokenKind.Is)
			return Precedence.TryGetValue(token.Text, out precedence) || (precedence = 7) == 7;
		return token.Kind == TokenKind.Operator && Precedence.TryGetValue(token.Text, out precedence);
	}

	private Token Consume(TokenKind kind, string message)
	{
		if (Current.Kind == kind)
			return Advance();
		Report("PAR003", message);
		return new Token(kind, string.Empty, new(Current.Span.Start, 0));
	}

	private bool Match(TokenKind kind)
	{
		if (Current.Kind != kind)
			return false;
		Advance();
		return true;
	}

	private Token Advance()
	{
		var token = Current;
		if (!AtEnd)
			_position++;
		return token;
	}

	private void Report(string code, string message) =>
		_diagnostics.Add(new Diagnostic(code, message, Current.Span));

	private void Report(string code, string message, TextSpan span) =>
		_diagnostics.Add(new Diagnostic(code, message, span));

	private void RecoverDeclaration()
	{
		while (!AtEnd && Current.Kind != TokenKind.Fun)
		{
			if (Current.Kind == TokenKind.Semicolon || Current.Kind == TokenKind.RightBrace)
			{
				Advance();
				break;
			}
			Advance();
		}
	}
}
