using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed partial class Parser
{
	private IReadOnlyList<ParameterNode> ParseParameters()
	{
		var parameters = new List<ParameterNode>();
		if (!Match(TokenKind.LeftParen))
			return parameters;
		while (!AtEnd && Current.Kind != TokenKind.RightParen)
		{
			parameters.Add(ParseParameter());
			if (!Match(TokenKind.Comma))
				break;
		}
		Consume(TokenKind.RightParen, "Ожидалась закрывающая скобка параметров");
		return parameters;
	}

	private ParameterNode ParseParameter()
	{
		var start = Current.Span.Start;

		// `$name` без типа: авто-инициализация поля параметром конструктора.
		if (Current.Kind == TokenKind.Dollar)
		{
			Advance();
			var autoName = Consume(TokenKind.Identifier, "Ожидалось имя параметра");
			var autoDefault = Match(TokenKind.Equals) ? ParseExpression() : null;
			return new ParameterNode(autoName.Text,
				new TextSpan(start, (autoDefault?.Span.End ?? autoName.Span.End) - start),
				null, autoDefault, IsAutoInit: true);
		}

		if (Current.Kind is TokenKind.Fun or TokenKind.LeftParen or TokenKind.LeftBracket ||
			(Current.Kind == TokenKind.Operator && Current.Text is "&" or "*") ||
			(Current.Kind == TokenKind.Identifier && Current.Text == "out" &&
				_position + 1 < _tokens.Count &&
				_tokens[_position + 1].Kind is not (TokenKind.Comma or TokenKind.RightParen)))
		{
			var compoundType = ParseTypeSyntax();
			var (compoundName, compoundAutoInit) = ParseParameterName();
			var compoundDefault = Match(TokenKind.Equals) ? ParseExpression() : null;
			return new ParameterNode(compoundName.Text,
				new TextSpan(start, (compoundDefault?.Span.End ?? compoundName.Span.End) - start),
				compoundType, compoundDefault, compoundAutoInit);
		}

		var first = Consume(TokenKind.Identifier, "Ожидалось имя параметра или тип");
		if (Current.Kind is TokenKind.Comma or TokenKind.RightParen or TokenKind.Equals)
		{
			var untypedDefault = Match(TokenKind.Equals) ? ParseExpression() : null;
			return new ParameterNode(first.Text,
				new TextSpan(first.Span.Start, (untypedDefault?.Span.End ?? first.Span.End) - first.Span.Start),
				null, untypedDefault);
		}

		// Дальше `first` начинает тип: `str $name`, `i32 value`, `list<i32> xs`.
		var parameterType = ParseTypeSyntaxFrom(first);
		var (name, isAutoInit) = ParseParameterName();
		var defaultValue = Match(TokenKind.Equals) ? ParseExpression() : null;
		return new ParameterNode(name.Text,
			new TextSpan(parameterType.Span.Start, (defaultValue?.Span.End ?? name.Span.End) - parameterType.Span.Start),
			parameterType, defaultValue, isAutoInit);
	}

	/// <summary>Имя параметра; необязательный <c>$</c> перед ним включает авто-инициализацию поля.</summary>
	private (Token Name, bool IsAutoInit) ParseParameterName()
	{
		var isAutoInit = Current.Kind == TokenKind.Dollar;
		if (isAutoInit)
			Advance();
		return (Consume(TokenKind.Identifier, "Ожидалось имя параметра"), isAutoInit);
	}
}
