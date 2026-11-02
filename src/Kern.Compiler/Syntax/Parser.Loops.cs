using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed partial class Parser
{
	private LoopExpression ParseLoopExpression()
	{
		var start = Current.Span.Start;
		var isAsync = Match(TokenKind.Async);
		Consume(TokenKind.Loop, "Ожидалось ключевое слово loop после async");
		var label = ParseLoopLabel();
		if (Current.Kind == TokenKind.LeftBrace)
		{
			var body = ParseBlock();
			if (Match(TokenKind.Colon))
			{
				var doWhileCondition = ParseExpression();
				return new LoopExpression(LoopExpressionKind.DoWhile, isAsync, [], null, doWhileCondition, body,
					new TextSpan(start, doWhileCondition.Span.End - start), label);
			}
			return new LoopExpression(LoopExpressionKind.Infinite, isAsync, [], null, null, body,
				new TextSpan(start, body.Span.End - start), label);
		}

		if (isAsync)
		{
			if (TryParseLoopBindingList(out var asyncBindings))
			{
				Advance();
				return ParseLoopIteration(start, true, asyncBindings, label);
			}
			var asyncIterable = ParseExpression();
			return ParseLoopIteration(start, true, [], label, asyncIterable);
		}

		if (TryParseLoopBindingList(out var bindings))
		{
			Advance();
			return ParseLoopIteration(start, false, bindings, label);
		}
		if (Current.Kind == TokenKind.In)
		{
			Advance();
			return ParseLoopIteration(start, false, [], label);
		}

		var conditionExpression = ParseExpression();
		if (conditionExpression is RangeExpression)
			return ParseLoopIteration(start, false, [], label, conditionExpression);
		var condition = conditionExpression is ParenthesizedExpression parenthesized
			? parenthesized.Expression
			: conditionExpression;
		var loopBody = ParseLoopBody();
		return new LoopExpression(LoopExpressionKind.Condition, false, [], null, condition, loopBody,
			new TextSpan(start, loopBody.Span.End - start), label);
	}

	/// <summary>Необязательная метка <c>@name</c> сразу после <c>loop</c> (spec §3, раунд 27).</summary>
	private LoopLabel? ParseLoopLabel()
	{
		if (Current.Kind != TokenKind.At)
			return null;
		var at = Advance();
		var name = Consume(TokenKind.Identifier, "Ожидалось имя метки цикла после @");
		return new LoopLabel(name.Text, new TextSpan(at.Span.Start, name.Span.End - at.Span.Start));
	}

	private LoopExpression ParseLoopIteration(
		int start,
		bool isAsync,
		IReadOnlyList<LoopBindingNode> bindings,
		LoopLabel? label,
		ExpressionNode? parsedIterable = null)
	{
		var iterable = parsedIterable ?? ParseExpression();
		var body = isAsync && Current.Kind == TokenKind.Await
			? ParseAwaitLoopBody()
			: ParseLoopBody();
		return new LoopExpression(LoopExpressionKind.Iteration, isAsync, bindings, iterable, null, body,
			new TextSpan(start, body.Span.End - start), label);
	}

	private SyntaxNode ParseAwaitLoopBody()
	{
		var start = Advance().Span.Start;
		var expression = ParseExpression();
		return new UnaryExpression("await ", expression,
			new TextSpan(start, expression.Span.End - start));
	}

	private SyntaxNode ParseLoopBody()
	{
		if (Current.Kind == TokenKind.LeftBrace)
			return ParseBlock();
		if (Current.Kind == TokenKind.Identifier && Current.Text == "if")
			return ParseIfExpression(requireElse: false);
		// `loop … yield x` — оператор в теле без скобок (spec §6).
		if (Current.Kind == TokenKind.Yield)
			return ParseStatement();
		var expression = ParseExpression();
		if (IsAssignmentOperator(Current))
		{
			var assignment = Advance();
			var value = ParseExpression();
			return new AssignmentStatement(expression, assignment.Text, value,
				new TextSpan(expression.Span.Start, value.Span.End - expression.Span.Start));
		}
		return expression;
	}

	private bool TryParseLoopBindingList(out IReadOnlyList<LoopBindingNode> bindings)
	{
		var originalPosition = _position;
		var diagnosticsStart = _diagnostics.Count;
		var parsed = new List<LoopBindingNode>();
		if (Current.Kind == TokenKind.LeftParen)
		{
			Advance();
			while (!AtEnd && Current.Kind != TokenKind.RightParen)
			{
				if (!TryParseLoopBinding(out var binding))
				{
					DiscardDiagnosticsSince(diagnosticsStart);
					_position = originalPosition;
					bindings = [];
					return false;
				}
				parsed.Add(binding);
				if (!Match(TokenKind.Comma)) break;
			}
			if (Current.Kind != TokenKind.RightParen)
			{
				DiscardDiagnosticsSince(diagnosticsStart);
				_position = originalPosition;
				bindings = [];
				return false;
			}
			Advance();
		}
		else if (TryParseLoopBinding(out var binding))
			parsed.Add(binding);
		else
		{
			DiscardDiagnosticsSince(diagnosticsStart);
			_position = originalPosition;
			bindings = [];
			return false;
		}
		if (Current.Kind != TokenKind.In)
		{
			DiscardDiagnosticsSince(diagnosticsStart);
			_position = originalPosition;
			bindings = [];
			return false;
		}
		bindings = parsed;
		return true;
	}

	private bool TryParseLoopBinding(out LoopBindingNode binding)
	{
		var start = Current.Span.Start;
		var isMutable = Match(TokenKind.Var);
		TypeSyntax? type = null;
		if (!isMutable && Current.Kind == TokenKind.Identifier && _position + 1 < _tokens.Count)
		{
			if (_tokens[_position + 1].Kind == TokenKind.Identifier)
			{
				var typeToken = Advance();
				type = NamedTypeSyntax.Plain(typeToken.Text, typeToken.Span);
			}
			else if (_tokens[_position + 1].Kind == TokenKind.Operator &&
				_tokens[_position + 1].Text is "<" or "?" or "!")
				type = ParseTypeSyntax();
		}
		if (Current.Kind == TokenKind.Identifier)
		{
			var name = Advance();
			binding = new LoopBindingNode(type, name.Text, isMutable, name.Text == "_",
				new TextSpan(start, name.Span.End - start));
			return true;
		}
		binding = null!;
		return false;
	}
}
