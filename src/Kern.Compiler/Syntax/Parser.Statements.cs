using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed partial class Parser
{
	private UseStatement ParseUseStatement()
	{
		var start = Consume(TokenKind.Use, "Ожидалось ключевое слово use").Span.Start;
		TypeSyntax? type = null;
		var isInferred = Match(TokenKind.Var);
		if (!isInferred)
			type = ParseTypeSyntax();
		var name = Consume(TokenKind.Identifier, "Ожидалось имя resource-переменной");
		Consume(TokenKind.Equals, "Ожидался = после имени resource-переменной");
		ExpressionNode initializer;
		if (Current.Kind == TokenKind.Await)
		{
			var awaitToken = Advance();
			var operand = ParseExpression();
			initializer = new UnaryExpression("await", operand,
				new TextSpan(awaitToken.Span.Start, operand.Span.End - awaitToken.Span.Start));
		}
		else
			initializer = ParseExpression();
		return new UseStatement(type, name.Text, initializer, isInferred,
			new TextSpan(start, initializer.Span.End - start));
	}

	private StatementNode ParseStatement()
	{
		// `async fun f` — локальная async-функция: модификатор сохраняется в объявлении,
		// поддержка (или честная диагностика) решается байндером.
		DeclarationModifiers? localModifiers = null;
		if (Current.Kind == TokenKind.Async && _position + 1 < _tokens.Count && _tokens[_position + 1].Kind == TokenKind.Fun)
		{
			Advance();
			localModifiers = new DeclarationModifiers { IsAsync = true };
		}

		if (Current.Kind == TokenKind.Fun && _position + 1 < _tokens.Count && _tokens[_position + 1].Kind == TokenKind.Identifier)
		{
			var function = ParseFunction(modifiers: localModifiers);
			return new LocalFunctionStatement(function, function.Span);
		}
		if (Current.Kind == TokenKind.Use)
			return ParseUseStatement();
		// `var (a, b) = tuple` — именованная форма деструктуризации (spec §3/§4):
		// типы целей выводятся из элементов кортежа.
		if (Current.Kind == TokenKind.Var && _position + 1 < _tokens.Count &&
			_tokens[_position + 1].Kind == TokenKind.LeftParen)
		{
			Advance();
			return ParseTupleDeclaration();
		}
		if (Current.Kind == TokenKind.LeftParen && IsTupleDeclaration())
			return ParseTupleDeclaration();
		if (Current.Kind == TokenKind.LeftBrace)
			return ParseBlock();
		if (Current.Kind == TokenKind.Var || IsTypedLocalDeclaration() || IsFunctionTypedLocalDeclaration())
			return ParseLocalDeclaration();
		if (Current.Kind == TokenKind.Loop || Current.Kind == TokenKind.Async &&
			_position + 1 < _tokens.Count && _tokens[_position + 1].Kind == TokenKind.Loop)
		{
			var loop = ParseLoopExpression();
			if (!loop.IsAsync && loop.Kind is LoopExpressionKind.Iteration or LoopExpressionKind.Infinite)
			{
				// Тело без скобок — узел оператора или выражения: присваивание и `ret`
				// нельзя приводить к ExpressionNode.
				StatementNode body = loop.Body switch
				{
					StatementNode statement => statement,
					ExpressionNode expression => new ExpressionStatement(expression, expression.Span),
					_ => new BlockStatement([], loop.Body.Span),
				};
				var loopBody = loop.Body as BlockStatement ?? new BlockStatement([body], loop.Body.Span);
				return new LoopStatement(loopBody, loop.Span, loop, loop.Label);
			}
			return new ExpressionStatement(loop, loop.Span);
		}
		if (Current.Kind == TokenKind.Ret)
		{
			var start = Advance().Span.Start;
			ExpressionNode? expression = null;
			if (Current.Kind is not (TokenKind.Semicolon or TokenKind.RightBrace or TokenKind.EndOfFile))
				expression = ParseExpression();
			var end = expression?.Span.End ?? Previous.Span.End;
			return new ReturnStatement(expression, new TextSpan(start, end - start));
		}
		if (Current.Kind == TokenKind.Yield)
		{
			var token = Advance();
			if (_yieldScopes.Count > 0)
			{
				int count = _yieldScopes.Pop();
				_yieldScopes.Push(count + 1);
			}

			ExpressionNode? value = null;
			if (Current.Kind is not (TokenKind.Semicolon or TokenKind.RightBrace or TokenKind.EndOfFile))
				value = ParseExpression();
			var end = value?.Span.End ?? token.Span.End;
			return new YieldStatement(value, new TextSpan(token.Span.Start, end - token.Span.Start));
		}
		if (Current.Kind is TokenKind.Stop or TokenKind.Next)
		{
			var token = Advance();
			// Метка-цель допустима только сразу на той же строке: `stop` + отдельный
			// оператор на следующей строке остаётся обычным `stop`.
			string? target = null;
			var targetSpan = default(TextSpan);
			var end = token.Span.End;
			if (Current.Kind == TokenKind.Identifier &&
				!HasNewlineBetween(token.Span.End, Current.Span.Start))
			{
				var targetToken = Advance();
				target = targetToken.Text;
				targetSpan = targetToken.Span;
				end = targetToken.Span.End;
			}
			var span = new TextSpan(token.Span.Start, end - token.Span.Start);
			return token.Kind == TokenKind.Stop
				? new StopStatement(span, target, targetSpan)
				: new NextStatement(span, target, targetSpan);
		}
		if (Current.Kind == TokenKind.Identifier && Current.Text == "if")
		{
			var ifExpression = ParseIfExpression(requireElse: false);
			return new ExpressionStatement(ifExpression, ifExpression.Span);
		}
		if (Current.Kind == TokenKind.Match)
		{
			var match = ParseMatchExpression();
			return new ExpressionStatement(match, match.Span);
		}
		if (Current.Kind == TokenKind.Identifier && Current.Text == "if")
		{
			var ifExpression = ParseIfExpression(requireElse: false);
			return new ExpressionStatement(ifExpression, ifExpression.Span);
		}
		var statementExpression = ParseExpression();
		if (Current.Kind == TokenKind.Comma)
		{
			var targets = new List<ExpressionNode> { statementExpression };
			while (Match(TokenKind.Comma))
				targets.Add(ParseExpression());
			if (Match(TokenKind.Equals))
			{
				var values = new List<ExpressionNode> { ParseExpression() };
				while (Match(TokenKind.Comma))
					values.Add(ParseExpression());
				var targetTuple = new TupleExpression(targets, new TextSpan(targets[0].Span.Start, targets[^1].Span.End - targets[0].Span.Start));
				var valueTuple = new TupleExpression(values, new TextSpan(values[0].Span.Start, values[^1].Span.End - values[0].Span.Start));
				return new AssignmentStatement(targetTuple, "=", valueTuple,
					new TextSpan(targetTuple.Span.Start, valueTuple.Span.End - targetTuple.Span.Start));
			}
			Report("PAR003", "После списка целей ожидался оператор =");
			return new ExpressionStatement(statementExpression, statementExpression.Span);
		}
		if (IsAssignmentOperator(Current))
		{
			var assignmentOperator = Advance();
			var value = ParseExpression();
			return new AssignmentStatement(statementExpression, assignmentOperator.Text, value,
				new TextSpan(statementExpression.Span.Start, value.Span.End - statementExpression.Span.Start));
		}
		return new ExpressionStatement(statementExpression, statementExpression.Span);
	}

	private bool LoopHasDoWhileSuffix()
	{
		if (_position + 1 >= _tokens.Count || _tokens[_position + 1].Kind != TokenKind.LeftBrace)
			return false;
		var depth = 0;
		for (var index = _position + 1; index < _tokens.Count; index++)
		{
			if (_tokens[index].Kind == TokenKind.LeftBrace) depth++;
			else if (_tokens[index].Kind == TokenKind.RightBrace && --depth == 0)
				return index + 1 < _tokens.Count && _tokens[index + 1].Kind == TokenKind.Colon;
		}
		return false;
	}

	private bool HasNewlineBetween(int start, int end)
	{
		for (var index = Math.Max(0, start); index < end && index < _source.Length; index++)
			if (_source[index] is '\r' or '\n')
				return true;
		return false;
	}

	private bool IsTupleDeclaration()
	{
		var index = _position + 1;
		var depth = 1;
		var genericDepth = 0;
		var hasComma = false;
		while (index < _tokens.Count && depth > 0)
		{
			var token = _tokens[index];
			if (token.Kind == TokenKind.Operator) genericDepth += GenericDepthDelta(token);
			else if (genericDepth == 0 && token.Kind == TokenKind.LeftParen) depth++;
			else if (genericDepth == 0 && token.Kind == TokenKind.RightParen) depth--;
			else if (depth == 1 && genericDepth == 0 && token.Kind == TokenKind.Comma) hasComma = true;
			index++;
		}
		if (!hasComma || index >= _tokens.Count)
			return false;
		if (_tokens[index].Kind == TokenKind.Identifier)
			index++;
		return index < _tokens.Count && _tokens[index].Kind == TokenKind.Equals;
	}

	private IReadOnlyList<TupleBindingNode> ParseNestedTupleBindings()
	{
		Consume(TokenKind.LeftParen, "Ожидалась вложенная деструктуризация");
		var bindings = new List<TupleBindingNode>();
		while (!AtEnd && Current.Kind != TokenKind.RightParen)
		{
			var start = Current.Span.Start;
			if (Current.Kind == TokenKind.LeftParen)
			{
				var nested = ParseNestedTupleBindings();
				bindings.Add(new TupleBindingNode(null, null, false,
					new TextSpan(start, Previous.Span.End - start))
				{
					Nested = nested,
				});
			}
			else if (Current.Kind == TokenKind.Identifier && Current.Text == "_")
			{
				var ignored = Advance();
				bindings.Add(new TupleBindingNode(null, null, true, ignored.Span));
			}
			else if (Current.Kind == TokenKind.Operator && Current.Text == "..")
			{
				var restStart = Advance().Span.Start;
				var restName = Consume(TokenKind.Identifier, "Ожидалось имя rest-связывания");
				bindings.Add(new TupleBindingNode(null, restName.Text, false,
					new TextSpan(restStart, restName.Span.End - restStart), restName.Text));
			}
			else
			{
				var first = Consume(TokenKind.Identifier, "Ожидалось имя или тип элемента кортежа");
				if (Current.Kind == TokenKind.Operator && Current.Text == "<")
				{
					var type = ParseTypeSyntaxFrom(first);
					var name = Consume(TokenKind.Identifier, "Ожидалось имя элемента кортежа");
					bindings.Add(new TupleBindingNode(type, name.Text, false,
						new TextSpan(start, name.Span.End - start)));
				}
				else if (Current.Kind == TokenKind.Identifier)
				{
					var name = Advance();
					bindings.Add(new TupleBindingNode(NamedTypeSyntax.Plain(first.Text, first.Span), name.Text,
						false, new TextSpan(start, name.Span.End - start)));
				}
				else
				{
					bindings.Add(new TupleBindingNode(null, first.Text, false, first.Span));
				}
			}

			if (!Match(TokenKind.Comma))
				break;
		}
		Consume(TokenKind.RightParen, "Ожидалась закрывающая скобка вложенной деструктуризации");
		return bindings;
	}

	private TupleDeclarationStatement ParseTupleDeclaration()
	{
		var open = Consume(TokenKind.LeftParen, "Ожидалась открывающая скобка кортежа");
		var bindings = new List<TupleBindingNode>();
		var closeIndex = _position;
		var depth = 1;
		var closeGenericDepth = 0;
		while (closeIndex < _tokens.Count && depth > 0)
		{
			var token = _tokens[closeIndex];
			if (token.Kind == TokenKind.Operator) closeGenericDepth += GenericDepthDelta(token);
			else if (closeGenericDepth == 0 && token.Kind == TokenKind.LeftParen) depth++;
			else if (closeGenericDepth == 0 && token.Kind == TokenKind.RightParen) depth--;
			closeIndex++;
		}
		var hasDeclarationName = closeIndex < _tokens.Count && _tokens[closeIndex].Kind == TokenKind.Identifier;
		while (!AtEnd && Current.Kind != TokenKind.RightParen)
		{
			var start = Current.Span.Start;
			if (Current.Kind == TokenKind.LeftParen)
			{
				bindings.Add(new TupleBindingNode(null, null, false, new TextSpan(start, 0))
				{
					Nested = ParseNestedTupleBindings(),
				});
				if (bindings[^1].Span.Length == 0)
				{
					var nested = bindings[^1];
					bindings[^1] = nested with
					{
						Span = new TextSpan(start, Previous.Span.End - start),
					};
				}
			}
			else if (Current.Kind == TokenKind.Operator && Current.Text == "..")
			{
				var restStart = Advance().Span.Start;
				TypeSyntax? type = null;
				if (Current.Kind == TokenKind.Identifier && _position + 1 < _tokens.Count &&
					(_tokens[_position + 1].Kind == TokenKind.Identifier || _tokens[_position + 1].Kind == TokenKind.Operator && _tokens[_position + 1].Text == "<"))
					type = ParseTypeSyntax();
				var restName = Consume(TokenKind.Identifier, "Ожидалось имя rest-элемента");
				bindings.Add(new TupleBindingNode(type, restName.Text, false,
					new TextSpan(restStart, restName.Span.End - restStart), restName.Text));
			}
			else if (Current.Kind == TokenKind.Identifier && Current.Text == "_")
			{
				var ignored = Advance();
				bindings.Add(new TupleBindingNode(null, null, true, ignored.Span));
			}
			else
			{
				var first = Consume(TokenKind.Identifier, "Ожидалось имя или тип элемента кортежа");
				TypeSyntax? type = null;
				string? name = null;
				if (Current.Kind == TokenKind.Operator && Current.Text == "<")
				{
					type = ParseTypeSyntaxFrom(first);
					if (Current.Kind == TokenKind.Operator && Current.Text == "..")
					{
						var restStart = Advance().Span.Start;
						var restName = Consume(TokenKind.Identifier, "Ожидалось имя rest-элемента");
					bindings.Add(new TupleBindingNode(type, restName.Text, false,
						new TextSpan(start, restName.Span.End - start), restName.Text));
					}
					else
					{
						var bindingName = Consume(TokenKind.Identifier, "Ожидалось имя элемента кортежа");
					bindings.Add(new TupleBindingNode(type, bindingName.Text, false,
						new TextSpan(start, bindingName.Span.End - start)));
					}
				}
				else if (Current.Kind == TokenKind.Operator && Current.Text == "..")
				{
					var restStart = Advance().Span.Start;
					var restName = Consume(TokenKind.Identifier, "Ожидалось имя rest-элемента");
					bindings.Add(new TupleBindingNode(NamedTypeSyntax.Plain(first.Text, first.Span), restName.Text, false,
						new TextSpan(start, restName.Span.End - start), restName.Text));
				}
				else if (Current.Kind == TokenKind.Identifier && Current.Text != "_")
				{
					type = NamedTypeSyntax.Plain(first.Text, first.Span);
					name = Advance().Text;
					bindings.Add(new TupleBindingNode(type, name, false,
						new TextSpan(start, Previous.Span.End - start)));
				}
				else if (hasDeclarationName)
					bindings.Add(new TupleBindingNode(NamedTypeSyntax.Plain(first.Text, first.Span), null, false,
						new TextSpan(start, first.Span.End - start)));
				else
					bindings.Add(new TupleBindingNode(null, first.Text, false, first.Span));
			}
			if (!Match(TokenKind.Comma)) break;
		}
		Consume(TokenKind.RightParen, "Ожидалась закрывающая скобка кортежа");
		string? declarationName = null;
		if (Current.Kind == TokenKind.Identifier)
			declarationName = Advance().Text;
		Consume(TokenKind.Equals, "Ожидался = после объявления кортежа");
		var initializer = ParseExpression();
		return new TupleDeclarationStatement(bindings, declarationName, initializer,
			new TextSpan(open.Span.Start, initializer.Span.End - open.Span.Start));
	}

	private bool IsTypedLocalDeclaration()
	{
		if (Current.Kind != TokenKind.Identifier)
			return false;
		var index = _position + 1;
		if (index >= _tokens.Count)
			return false;

		if (_tokens[index].Kind == TokenKind.Operator && _tokens[index].Text == "<")
		{
			var depth = 0;
			do
			{
				if (index >= _tokens.Count) return false;
				depth += GenericDepthDelta(_tokens[index]);
				index++;
			} while (depth > 0);
		}

		while (index < _tokens.Count && _tokens[index].Kind == TokenKind.Operator &&
			_tokens[index].Text is "?" or "!")
			index++;

		if (_tokens[index].Kind != TokenKind.Identifier)
			return false;

		// Тип и имя локальной переменной обязаны быть на одной строке: иначе две
		// последовательные строки-выражения (`count` / `count`) ошибочно считались
		// объявлением `<тип> <имя>`.
		if (index > 0 && HasNewlineBetween(_tokens[index - 1].Span.End, _tokens[index].Span.Start))
			return false;

		return
			(index + 1 >= _tokens.Count ||
			_tokens[index + 1].Kind is TokenKind.Equals or TokenKind.Comma or TokenKind.RightParen or
				TokenKind.Semicolon or TokenKind.LeftParen ||
			HasNewlineBetween(_tokens[index].Span.End, _tokens[index + 1].Span.Start));
	}

	/// <summary>
	/// Локальная переменная с функциональным типом: <c>fun(i32) -&gt; i32 f = …</c>.
	/// Отличие от лямбды-выражения — имя и <c>=</c>/<c>,</c> после <c>-&gt; R</c>, поэтому
	/// разбор типа идёт с откатом (диагностики пробного разбора отбрасываются).
	/// </summary>
	private bool IsFunctionTypedLocalDeclaration()
	{
		if (Current.Kind != TokenKind.Fun)
			return false;

		var savedPosition = _position;
		var diagnosticsCount = _diagnostics.Count;
		try
		{
			if (ParseTypeSyntax() is not FunctionTypeSyntax)
				return false;
			if (Current.Kind != TokenKind.Identifier)
				return false;
			var index = _position + 1;
			return index >= _tokens.Count ||
				_tokens[index].Kind is TokenKind.Equals or TokenKind.Comma or TokenKind.Semicolon ||
				HasNewlineBetween(_tokens[index - 1].Span.End, _tokens[index].Span.Start);
		}
		finally
		{
			_position = savedPosition;
			DiscardDiagnosticsSince(diagnosticsCount);
		}
	}

	private LocalDeclarationStatement ParseLocalDeclaration()
	{
		if (Current.Kind == TokenKind.Var)
		{
			var varStart = Advance().Span.Start;
			var varSingleAssignment = MatchOperator("!");
			var varName = Consume(TokenKind.Identifier, "Ожидалось имя локальной переменной");
			var varInitializer = Match(TokenKind.Equals) ? ParseExpression() : null;
			return new LocalDeclarationStatement(null, varName.Text, varInitializer, true,
				new TextSpan(varStart, (varInitializer?.Span.End ?? varName.Span.End) - varStart))
			{
				IsSingleAssignment = varSingleAssignment
			};
		}
		var start = Current.Span.Start;
		var type = ParseTypeSyntax();
		var name = Consume(TokenKind.Identifier, "Ожидалось имя локальной переменной");
		ExpressionNode? initializer;
		if (Match(TokenKind.Equals))
		{
			initializer = ParseExpression();
		}
		else if (Current.Kind == TokenKind.LeftParen)
		{
			// `Point a(1.0, 2.0)` — объявление с вызовом конструктора без `=` (spec §8).
			var typeText = type.ToString();
			var generic = typeText.IndexOf('<', StringComparison.Ordinal);
			if (generic >= 0)
				typeText = typeText[..generic];
			initializer = ParseCall(new IdentifierExpression(
				typeText.TrimEnd('?', '!'),
				type.Span));
		}
		else
		{
			initializer = null;
		}
		var additionalBindings = new List<LocalBindingNode>();
		var end = initializer?.Span.End ?? name.Span.End;
		while (Match(TokenKind.Comma))
		{
			var nextName = Consume(TokenKind.Identifier, "Ожидалось имя локальной переменной");
			ExpressionNode? nextInitializer = Match(TokenKind.Equals) ? ParseExpression() : null;
			end = nextInitializer?.Span.End ?? nextName.Span.End;
			additionalBindings.Add(new LocalBindingNode(nextName.Text, nextInitializer,
				new TextSpan(nextName.Span.Start, end - nextName.Span.Start)));
		}
		return new LocalDeclarationStatement(type, name.Text, initializer, false,
			new TextSpan(start, end - start), additionalBindings)
		{
			IsSingleAssignment = type is NamedTypeSyntax { IsSingleAssignment: true }
		};
	}

	private Token ConsumeMemberName(string message)
	{
		if (Current.Kind is TokenKind.Next or TokenKind.Project or TokenKind.Use or TokenKind.Stop or TokenKind.Type or TokenKind.Match)
			return Advance();
		return Consume(TokenKind.Identifier, message);
	}

	private static bool IsAssignmentOperator(Token token) =>
		token.Kind == TokenKind.Equals || token.Kind == TokenKind.Operator &&
		token.Text is "+=" or "-=" or "*=" or "/=" or "%=" or "**=" or "&=" or "|=" or "^=" or "<<=" or ">>=";

}
