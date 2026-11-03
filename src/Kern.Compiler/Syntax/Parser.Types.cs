using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed partial class Parser
{
	/// <summary>
	/// Полная грамматика типа: функциональный, кортеж, union, именованный тип
	/// с generic-аргументами и маркерами <c>?</c>/<c>!</c> в любом порядке.
	/// </summary>
	private TypeSyntax ParseTypeSyntax()
	{
		var start = Current.Span.Start;
		// `&T` (in), `*T`/`out T` (ref) — маркеры параметра (spec §6); допустимы только
		// там, проверку контекста делает TypeResolver/Collector.
		if (Current.Kind == TokenKind.Operator && Current.Text is "&" or "*")
		{
			var marker = Advance();
			TypeSyntax inner = ParseTypeSyntax();
			if (inner is NamedTypeSyntax named)
				return named with
				{
					IsIn = marker.Text == "&",
					IsRef = marker.Text == "*",
					Span = new TextSpan(marker.Span.Start, named.Span.End - marker.Span.Start),
				};
			Report("PAR003", "Маркер '&' или '*' допустим только перед именованным типом", marker.Span);
			return inner;
		}

		// `out T` — синоним `*T` (роль out выполняет изменяемая ссылка, spec §6).
		if (Current.Kind == TokenKind.Identifier && Current.Text == "out")
		{
			var marker = Advance();
			TypeSyntax inner = ParseTypeSyntax();
			if (inner is NamedTypeSyntax named)
				return named with
				{
					IsRef = true,
					Span = new TextSpan(marker.Span.Start, named.Span.End - marker.Span.Start),
				};
			Report("PAR003", "Маркер 'out' допустим только перед именованным типом", marker.Span);
			return inner;
		}

		return Current.Kind switch
		{
			TokenKind.Fun => ParseFunctionTypeSyntax(),
			TokenKind.LeftParen => ParseTupleTypeSyntax(),
			TokenKind.LeftBracket => ParseUnionTypeSyntaxOutsideAlias(),
			TokenKind.Identifier => ParseNamedTypeSyntax(),
			_ => ParseMissingTypeSyntax(start)
		};
	}

	/// <summary>Восстанавливается после уже прочитанного первого токена типа (например, имени).</summary>
	private TypeSyntax ParseTypeSyntaxFrom(Token first)
	{
		_position--;
		return ParseTypeSyntax();
	}

	private NamedTypeSyntax ParseNamedTypeSyntax()
	{
		var name = Consume(TokenKind.Identifier, "Ожидалось имя типа");
		var arguments = new List<TypeSyntax>();
		var labels = new List<string?>();
		if (IsOperator("<"))
		{
			Advance();
			if (IsClosingAngle())
				Report("PAR003", "Ожидалось имя типа generic-аргумента");
			while (!AtEnd && !IsClosingAngle())
			{
				arguments.Add(ParseTypeSyntax());
				// Метка generic-аргумента: `result<T[ok], E[error]>` (spec §16). Семантику
				// (где и какие метки допустимы) проверяет TypeResolver.
				labels.Add(ParseArgumentLabel());
				if (!Match(TokenKind.Comma))
					break;
				if (IsClosingAngle())
				{
					Report("PAR003", "Ожидалось имя типа generic-аргумента");
					break;
				}
			}
			if (!MatchClosingAngle())
				Report("PAR003", "Ожидалась закрывающая угловая скобка списка generic-аргументов");
		}
		var nullable = false;
		var singleAssignment = false;
		while (IsOperator("?") || IsOperator("!"))
		{
			var marker = Advance();
			if (marker.Text == "?")
			{
				if (nullable)
					Report("PAR014", "Повторный маркер '?' в типе", marker.Span);
				nullable = true;
			}
			else
			{
				if (singleAssignment)
					Report("PAR014", "Повторный маркер '!' в типе", marker.Span);
				singleAssignment = true;
			}
		}
		var end = Math.Max(name.Span.End, Previous.Span.End);
		return new NamedTypeSyntax(name.Text, arguments, labels, nullable, singleAssignment,
			new TextSpan(name.Span.Start, end - name.Span.Start));
	}

	/// <summary>
	/// Необязательная метка generic-аргумента <c>[ok]</c>: хранится текст первого идентификатора,
	/// остальное до <c>]</c> пропускается для восстановления. Допустимость метки проверяет
	/// <see cref="Semantics.Types.TypeResolver"/> (spec §16).
	/// </summary>
	private string? ParseArgumentLabel()
	{
		if (Current.Kind != TokenKind.LeftBracket)
			return null;

		Advance();
		string? label = null;
		if (Current.Kind == TokenKind.Identifier)
			label = Advance().Text;
		while (!AtEnd && Current.Kind != TokenKind.RightBracket)
			Advance();
		Consume(TokenKind.RightBracket, "Ожидалась ] после метки generic-аргумента");
		return label;
	}

	private FunctionTypeSyntax ParseFunctionTypeSyntax()
	{
		var start = Consume(TokenKind.Fun, "Ожидалось ключевое слово fun").Span.Start;
		var parameters = new List<TypeSyntax>();
		if (Current.Kind == TokenKind.LeftParen)
		{
			Advance();
			while (!AtEnd && Current.Kind != TokenKind.RightParen)
			{
				parameters.Add(ParseTypeSyntax());
				if (!Match(TokenKind.Comma))
					break;
			}
			Consume(TokenKind.RightParen, "Ожидалась закрывающая скобка параметров функционального типа");
		}
		TypeSyntax? returnType = null;
		if (IsOperator("->"))
		{
			Advance();
			returnType = ParseTypeSyntax();
		}
		var end = Math.Max(start, Previous.Span.End);
		return new FunctionTypeSyntax(parameters, returnType, new TextSpan(start, end - start));
	}

	private TupleTypeSyntax ParseTupleTypeSyntax()
	{
		var open = Consume(TokenKind.LeftParen, "Ожидалась открывающая скобка типа-кортежа");
		var elements = new List<TupleTypeElementSyntax>();
		while (!AtEnd && Current.Kind != TokenKind.RightParen)
		{
			var elementStart = Current.Span.Start;
			var type = ParseTypeSyntax();
			string? name = null;
			if (Current.Kind == TokenKind.Identifier)
				name = Advance().Text;
			var elementEnd = Math.Max(elementStart, Previous.Span.End);
			elements.Add(new TupleTypeElementSyntax(type, name,
				new TextSpan(elementStart, elementEnd - elementStart)));
			if (!Match(TokenKind.Comma))
				break;
		}
		var close = Consume(TokenKind.RightParen, "Ожидалась закрывающая скобка типа-кортежа");
		var span = new TextSpan(open.Span.Start, Math.Max(0, close.Span.End - open.Span.Start));
		if (elements.Count == 0)
			Report("PAR015", "Кортеж должен содержать не менее двух элементов", span);
		else if (elements.Count == 1 && elements[0].Name is null)
			Report("PAR015", "Кортеж из одного элемента должен иметь имя", span);
		return new TupleTypeSyntax(elements, span);
	}

	/// <summary>
	/// Union-тип вне позиции алиаса (<c>type X = [...]</c>) запрещён: разбираем для восстановления,
	/// но сообщаем диагностику. Разрешение типа выполняется только в <see cref="ParseType"/>.
	/// </summary>
	private UnionTypeSyntax ParseUnionTypeSyntaxOutsideAlias()
	{
		var union = ParseUnionTypeSyntax();
		Report("PAR016", "Union-тип допустим только в объявлении type X = [...]", union.Span);
		return union;
	}

	private UnionTypeSyntax ParseUnionTypeSyntax()
	{		var open = Consume(TokenKind.LeftBracket, "Ожидалась [ перед вариантами union-типа");
		var alternatives = new List<TypeSyntax>();
		if (Current.Kind == TokenKind.RightBracket)
			Report("PAR003", "Ожидался хотя бы один вариант union-типа");
		while (!AtEnd && Current.Kind != TokenKind.RightBracket)
		{
			var before = _position;
			alternatives.Add(ParseTypeSyntax());
			if (Current.Kind == TokenKind.RightBracket)
				break;
			if (!Match(TokenKind.Comma))
			{
				Report("PAR003", "Ожидалась запятая между вариантами union-типа");
				if (Current.Kind != TokenKind.RightBracket)
					Advance();
			}
			if (_position == before)
				Advance();
		}
		var close = Consume(TokenKind.RightBracket, "Ожидалась ] после вариантов union-типа");
		return new UnionTypeSyntax(alternatives,
			new TextSpan(open.Span.Start, Math.Max(0, close.Span.End - open.Span.Start)));
	}

	/// <summary>Список generic-параметров объявления вместе с ограничениями.</summary>
	private IReadOnlyList<GenericParameterNode> ParseGenericParameterList()
	{
		var parameters = new List<GenericParameterNode>();
		if (!MatchOperator("<"))
			return parameters;
		if (IsClosingAngle())
			Report("PAR012", "Ожидался хотя бы один generic-параметр");
		while (!AtEnd && !IsClosingAngle())
		{
			var start = Current.Span.Start;
			if (Current.Kind != TokenKind.Identifier)
			{
				Report("PAR012", "Ожидалось имя generic-параметра");
				if (!IsClosingAngle())
					Advance();
				break;
			}
			var name = Advance();
			var constraints = new List<TypeSyntax>();
			var hasCtorConstraint = false;
			IReadOnlyList<TypeSyntax> ctorConstraintArguments = [];
			if (Current.Kind == TokenKind.LeftBracket)
			{
				Advance();
				if (Current.Kind == TokenKind.RightBracket)
					Report("PAR012", "Ожидалось ограничение generic-параметра");
				while (!AtEnd && Current.Kind != TokenKind.RightBracket)
				{
					if (Current.Kind == TokenKind.Ctor)
					{
						Advance();
						var ctorArguments = new List<TypeSyntax>();
						if (Match(TokenKind.LeftParen))
						{
							while (!AtEnd && Current.Kind != TokenKind.RightParen)
							{
								ctorArguments.Add(ParseTypeSyntax());
								if (!Match(TokenKind.Comma))
									break;
							}
							Consume(TokenKind.RightParen, "Ожидалась ) после аргументов ctor");
						}
						ctorConstraintArguments = ctorArguments;
						hasCtorConstraint = true;
					}
					else
						constraints.Add(ParseTypeSyntax());
					if (!Match(TokenKind.Comma))
						break;
				}
				Consume(TokenKind.RightBracket, "Ожидалась ] после ограничений generic-параметра");
			}
			parameters.Add(new GenericParameterNode(name.Text, constraints, hasCtorConstraint,
				new TextSpan(start, Math.Max(0, Previous.Span.End - start)), ctorConstraintArguments));
			if (!Match(TokenKind.Comma))
				break;
		}
		if (!MatchClosingAngle())
			Report("PAR012", "Ожидалась закрывающая угловая скобка списка generic-параметров");
		return parameters;
	}

	/// <summary>Список родителей: <c>[A, B(x, 0), async_iter&lt;i32&gt;]</c>.</summary>
	private IReadOnlyList<ParentTypeNode> ParseParentList()
	{
		var parents = new List<ParentTypeNode>();
		if (!Match(TokenKind.LeftBracket))
			return parents;
		while (!AtEnd && Current.Kind != TokenKind.RightBracket)
		{
			var start = Current.Span.Start;
			var type = ParseTypeSyntax();
			var arguments = new List<ExpressionNode>();
			var hasArgumentList = false;
			if (Current.Kind == TokenKind.LeftParen)
			{
				hasArgumentList = true;
				Advance();
				while (!AtEnd && Current.Kind != TokenKind.RightParen)
				{
					arguments.Add(ParseExpression());
					if (!Match(TokenKind.Comma))
						break;
				}
				Consume(TokenKind.RightParen, "Ожидалась закрывающая скобка аргументов родителя");
			}
			parents.Add(new ParentTypeNode(type, arguments, hasArgumentList,
				new TextSpan(start, Math.Max(0, Previous.Span.End - start))));
			if (!Match(TokenKind.Comma))
				break;
		}
		Consume(TokenKind.RightBracket, "Ожидалась ] после списка родителей типа");
		return parents;
	}

	private TypeSyntax ParseMissingTypeSyntax(int start)
	{
		Report("PAR011", "Ожидалось имя типа");
		return NamedTypeSyntax.Plain("<ошибка>", new TextSpan(start, 0));
	}

	private bool IsOperator(string text) =>
		Current.Kind == TokenKind.Operator && Current.Text == text;

	/// <summary>
	/// Закрывающая скобка списка generic-аргументов. Лексер выдаёт `>>` одним токеном
	/// (оператор сдвига), поэтому в разборе типа он расщепляется на два `>`; разбор
	/// выражений (`a >> b`) токены не трогает.
	/// </summary>
	private void SplitClosingAngle()
	{
		var token = Current;
		if (token.Kind != TokenKind.Operator || token.Text != ">>")
			return;
		_tokens[_position] = new Token(TokenKind.Operator, ">", new TextSpan(token.Span.Start, 1));
		_tokens.Insert(_position + 1, new Token(TokenKind.Operator, ">", new TextSpan(token.Span.Start + 1, 1)));
	}

	/// <summary>Текущий токен — `>` (в том числе первая половина `>>`, см. <see cref="SplitClosingAngle"/>).</summary>
	private bool IsClosingAngle()
	{
		SplitClosingAngle();
		return IsOperator(">");
	}

	private bool MatchClosingAngle()
	{
		if (!IsClosingAngle())
			return false;
		Advance();
		return true;
	}

	/// <summary>Убирает диагностики, выданные при пробном разборе, который затем откатили.</summary>
	private void DiscardDiagnosticsSince(int count)
	{
		if (_diagnostics.Count > count)
			_diagnostics.RemoveRange(count, _diagnostics.Count - count);
	}

	private bool MatchOperator(string text)
	{
		if (!IsOperator(text))
			return false;
		Advance();
		return true;
	}
}
