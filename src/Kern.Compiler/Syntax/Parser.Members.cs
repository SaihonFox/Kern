using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed partial class Parser
{
	/// <summary>Объявление, перед которым могут стоять модификаторы; задаёт допустимый набор.</summary>
	private enum ModifierTarget
	{
		TopLevelType,
		NestedType,
		TopLevelFunction,
		MemberFunction,
		Unit,
		Use,
		Extension,
		GlobalField,
		Constructor,
		Member,
		ParentSelection,
		Operator
	}

	private enum ModifierKind
	{
		Access,
		Abstract,
		Static,
		Override,
		Open,
		Async
	}

	private readonly record struct ModifierToken(
		ModifierKind Kind,
		TextSpan Span,
		Accessibility Accessibility = Accessibility.Private);

	private sealed class DeclarationModifiers
	{
		public Accessibility Accessibility { get; set; } = Accessibility.Private;
		public bool HasAccessibility { get; set; }
		public bool IsAbstract { get; set; }
		public bool IsStatic { get; set; }
		public bool IsOverride { get; set; }
		public bool IsOpen { get; set; }
		public bool IsAsync { get; set; }
	}

	/// <summary>
	/// Читает модификаторы в любом порядке: <c>pub/priv/prot/int</c>, <c>@</c>, <c>#</c>,
	/// <c>~</c>, <c>open</c>, <c>async</c>. Проверка допустимости — в <see cref="BuildModifiers"/>.
	/// </summary>
	private List<ModifierToken> ParseModifierTokens()
	{
		var modifiers = new List<ModifierToken>();
		while (!AtEnd)
		{
			var token = Current;
			ModifierToken? modifier = null;
			if (token.Kind == TokenKind.Identifier)
			{
				if (token.Text == "pub")
					modifier = new ModifierToken(ModifierKind.Access, token.Span, Accessibility.Public);
				else if (token.Text == "priv")
					modifier = new ModifierToken(ModifierKind.Access, token.Span, Accessibility.Private);
				else if (token.Text == "prot")
					modifier = new ModifierToken(ModifierKind.Access, token.Span, Accessibility.Protected);
				else if (token.Text == "int")
					modifier = new ModifierToken(ModifierKind.Access, token.Span, Accessibility.Internal);
			}
			else if (token.Kind == TokenKind.At)
				modifier = new ModifierToken(ModifierKind.Abstract, token.Span);
			else if (token.Kind == TokenKind.Hash)
				modifier = new ModifierToken(ModifierKind.Static, token.Span);
			else if (token.Kind == TokenKind.Operator && token.Text == "~")
				modifier = new ModifierToken(ModifierKind.Override, token.Span);
			else if (token.Kind == TokenKind.Open)
				modifier = new ModifierToken(ModifierKind.Open, token.Span);
			else if (token.Kind == TokenKind.Async)
				modifier = new ModifierToken(ModifierKind.Async, token.Span);
			if (modifier is null)
				break;
			Advance();
			modifiers.Add(modifier.Value);
		}
		return modifiers;
	}

	/// <summary>Проверяет модификаторы для конкретного вида объявления и собирает флаги.</summary>
	private DeclarationModifiers BuildModifiers(
		IReadOnlyList<ModifierToken> tokens,
		ModifierTarget target,
		Accessibility defaultAccessibility)
	{
		var result = new DeclarationModifiers { Accessibility = defaultAccessibility };
		foreach (var token in tokens)
		{
			switch (token.Kind)
			{
				case ModifierKind.Access:
					if (!AllowsAccessibility(target))
						Report("PAR017", "Модификатор доступа недопустим у use", token.Span);
					else if (result.HasAccessibility)
						Report("PAR017", "Повторный модификатор доступа", token.Span);
					else
					{
						result.HasAccessibility = true;
						result.Accessibility = token.Accessibility;
					}
					break;
				case ModifierKind.Abstract:
					if (!AllowsAbstract(target))
						Report("PAR017", "Модификатор @ допустим только у type и fun", token.Span);
					else if (result.IsAbstract)
						Report("PAR017", "Повторный модификатор @", token.Span);
					else
						result.IsAbstract = true;
					break;
				case ModifierKind.Static:
					if (!AllowsStatic(target))
						Report("PAR017", "Модификатор # допустим только у члена типа", token.Span);
					else if (result.IsStatic)
						Report("PAR017", "Повторный модификатор #", token.Span);
					else
						result.IsStatic = true;
					break;
				case ModifierKind.Override:
					if (!AllowsOverride(target))
						Report("PAR017", "Модификатор ~ допустим только у метода, поля или свойства типа", token.Span);
					else if (result.IsOverride)
						Report("PAR017", "Повторный модификатор ~", token.Span);
					else
						result.IsOverride = true;
					break;
				case ModifierKind.Open:
					if (!AllowsOpen(target))
						Report("PAR017", "Модификатор open допустим только у type и fun", token.Span);
					else if (result.IsOpen)
						Report("PAR017", "Повторный модификатор open", token.Span);
					else
						result.IsOpen = true;
					break;
				case ModifierKind.Async:
					if (!AllowsAsync(target))
						Report("PAR017", "Модификатор async допустим только у fun", token.Span);
					else if (result.IsAsync)
						Report("PAR017", "Повторный модификатор async", token.Span);
					else
						result.IsAsync = true;
					break;
			}
		}

		if (result.IsStatic && result.IsOverride)
			Report("PAR017", "Модификаторы # и ~ несовместимы", FirstSpan(tokens, ModifierKind.Static, ModifierKind.Override));
		if (result.IsStatic && result.IsAbstract)
			Report("PAR017", "Модификаторы # и @ несовместимы", FirstSpan(tokens, ModifierKind.Static, ModifierKind.Abstract));
		if (result.IsAbstract && result.IsOverride)
			Report("PAR017", "Модификаторы @ и ~ несовместимы", FirstSpan(tokens, ModifierKind.Abstract, ModifierKind.Override));

		return result;
	}

	private static bool AllowsAccessibility(ModifierTarget target) => target != ModifierTarget.Use;

	private static bool AllowsAbstract(ModifierTarget target) =>
		target is ModifierTarget.TopLevelType or ModifierTarget.NestedType or
			ModifierTarget.TopLevelFunction or ModifierTarget.MemberFunction;

	private static bool AllowsStatic(ModifierTarget target) =>
		target is ModifierTarget.NestedType or ModifierTarget.MemberFunction or ModifierTarget.Member;

	private static bool AllowsOverride(ModifierTarget target) =>
		target is ModifierTarget.MemberFunction or ModifierTarget.Member or ModifierTarget.Operator;

	private static bool AllowsOpen(ModifierTarget target) =>
		target is ModifierTarget.TopLevelType or ModifierTarget.NestedType or
			ModifierTarget.TopLevelFunction or ModifierTarget.MemberFunction;

	private static bool AllowsAsync(ModifierTarget target) =>
		target is ModifierTarget.TopLevelFunction or ModifierTarget.MemberFunction;

	private static TextSpan FirstSpan(IReadOnlyList<ModifierToken> tokens, ModifierKind first, ModifierKind second)
	{
		foreach (var token in tokens)
			if (token.Kind == first || token.Kind == second)
				return token.Span;
		return default;
	}

	private IReadOnlyList<SyntaxNode> ParseTypeMembers()
	{
		var members = new List<SyntaxNode>();
		if (!Match(TokenKind.LeftBrace)) return members;
		while (!AtEnd && Current.Kind != TokenKind.RightBrace)
		{
			var before = _position;
			var attributes = ParseAttributes();
			var modifiers = ParseModifierTokens();
			if (Current.Kind == TokenKind.Fun && _position + 1 < _tokens.Count && _tokens[_position + 1].Kind == TokenKind.Dollar)
				members.Add(ParseParentMethodSelection(modifiers, Current.Span.Start, attributes));
			else if (Current.Kind == TokenKind.Fun)
				members.Add(ParseFunction(attributes, BuildModifiers(modifiers, ModifierTarget.MemberFunction, Accessibility.Private)));
			else if (Current.Kind == TokenKind.Type)
				members.Add(ParseType(attributes, BuildModifiers(modifiers, ModifierTarget.NestedType, Accessibility.Private)));
			else if (Current.Kind == TokenKind.Op)
				members.Add(ParseOperatorMember(modifiers, attributes));
			else if (Current.Kind == TokenKind.Ctor)
				members.Add(ParseConstructor(modifiers, attributes));
			else if (modifiers.Count > 0 &&
				Current.Kind is not (TokenKind.Identifier or TokenKind.LeftParen) &&
				!(Current.Kind == TokenKind.Operator && Current.Text == "!"))
			{
				Report("PAR003", "После модификаторов ожидалось объявление члена типа", Current.Span);
				Advance();
			}
			else
				members.Add(ParseFieldOrProperty(modifiers, attributes));
			Match(TokenKind.Semicolon);
			if (_position == before) Advance();
		}
		Consume(TokenKind.RightBrace, "Ожидалась закрывающая фигурная скобка типа");
		return members;
	}

	private ParentMethodSelectionNode ParseParentMethodSelection(
		IReadOnlyList<ModifierToken> modifierTokens,
		int start,
		IReadOnlyList<AttributeNode> attributes)
	{
		var modifiers = BuildModifiers(modifierTokens, ModifierTarget.ParentSelection, Accessibility.Private);
		Consume(TokenKind.Fun, "Ожидалось ключевое слово fun");
		var parentType = ParseParentSelectionPrefix();
		var name = Consume(TokenKind.Identifier, "Ожидалось имя метода родителя");
		var parameters = ParseParameters();
		TypeSyntax? returnType = null;
		if (Current.Kind == TokenKind.Operator && Current.Text == "->")
		{
			Advance();
			returnType = ParseTypeSyntax();
		}
		return new ParentMethodSelectionNode(parentType, name.Text, parameters, returnType,
			new TextSpan(start, Previous.Span.End - start), modifiers.Accessibility, modifiers.IsOverride,
			Attributes: attributes);
	}

	private string ParseParentSelectionPrefix()
	{
		Consume(TokenKind.Dollar, "Ожидался $ перед выбором родителя");
		Consume(TokenKind.LeftBracket, "Ожидалась [ после $");
		var parentType = Consume(TokenKind.Identifier, "Ожидалось имя родительского типа").Text;
		Consume(TokenKind.RightBracket, "Ожидалась ] после имени родителя");
		// Q19: `$[A]member` — без точки; старую `$[A]member` отклоняем.
		if (Current.Kind == TokenKind.Dot)
		{
			Report("PAR003", "Точка после $[A] запрещена: используйте $[A]member", Current.Span);
			Advance();
		}
		return parentType;
	}

	private SyntaxNode ParseFieldOrProperty(
		IReadOnlyList<ModifierToken> modifierTokens,
		IReadOnlyList<AttributeNode> attributes)
	{
		var start = Current.Span.Start;
		var required = Current.Kind == TokenKind.Operator && Current.Text == "!";
		if (required) Advance();
		var type = ParseTypeSyntax();
		if (Current.Kind == TokenKind.Dollar)
		{
			var parentType = ParseParentSelectionPrefix();
			var selectedName = Consume(TokenKind.Identifier, "Ожидалось имя поля родителя");
			var parentModifiers = BuildModifiers(modifierTokens, ModifierTarget.ParentSelection, Accessibility.Private);
			return new ParentFieldSelectionNode(type, parentType, selectedName.Text,
				new TextSpan(start, selectedName.Span.End - start),
				parentModifiers.Accessibility, parentModifiers.IsOverride, Attributes: attributes);
		}
		var name = Consume(TokenKind.Identifier, "Ожидалось имя поля");
		if (Current.Kind == TokenKind.LeftBrace)
		{
			var modifiers = BuildModifiers(modifierTokens, ModifierTarget.Member, Accessibility.Private);
			Advance();
			var accessors = ParsePropertyAccessors();
			var close = Consume(TokenKind.RightBrace, "Ожидалась закрывающая фигурная скобка свойства");
			ExpressionNode? initializer = Match(TokenKind.Equals) ? ParseExpression() : null;
			var end = initializer?.Span.End ?? close.Span.End;
			return new PropertyMemberNode(type, name.Text, accessors, null, initializer,
				new TextSpan(start, end - start), modifiers.IsStatic, modifiers.Accessibility, modifiers.IsOverride,
				Attributes: attributes);
		}
		if (Current.Kind == TokenKind.Operator && Current.Text == "=>")
		{
			var modifiers = BuildModifiers(modifierTokens, ModifierTarget.Member, Accessibility.Private);
			Advance();
			var expression = ParseExpression();
			return new PropertyMemberNode(type, name.Text, [], expression, null,
				new TextSpan(start, expression.Span.End - start),
				modifiers.IsStatic, modifiers.Accessibility, modifiers.IsOverride, Attributes: attributes);
		}
		var fieldModifiers = BuildModifiers(modifierTokens, ModifierTarget.Member, Accessibility.Private);
		ExpressionNode? fieldInitializer = Match(TokenKind.Equals) ? ParseExpression() : null;
		return new FieldMemberNode(type, name.Text, required, fieldInitializer,
			new TextSpan(start, (fieldInitializer?.Span.End ?? name.Span.End) - start),
			fieldModifiers.IsStatic, fieldModifiers.Accessibility, fieldModifiers.IsOverride, Attributes: attributes);
	}

	private IReadOnlyList<PropertyAccessorNode> ParsePropertyAccessors()
	{
		var accessors = new List<PropertyAccessorNode>();
		while (!AtEnd && Current.Kind != TokenKind.RightBrace)
		{
			var before = _position;
			var accessorToken = Consume(TokenKind.Identifier, "Ожидался accessor свойства");
			var kind = AccessorKindOf(accessorToken.Text);
			if (kind is null)
			{
				Report("PAR008", "Ожидался accessor get, set или init", accessorToken.Span);
				if (Current.Kind == TokenKind.LeftParen)
					SkipBalancedParentheses();
				ParseAccessorBody();
			}
			else
				accessors.Add(ParsePropertyAccessor(kind.Value, accessorToken));
			Match(TokenKind.Semicolon);
			if (_position == before) Advance();
		}
		return accessors;
	}

	private static AccessorKind? AccessorKindOf(string text) => text switch
	{
		"get" => AccessorKind.Get,
		"set" => AccessorKind.Set,
		"init" => AccessorKind.Init,
		_ => null
	};

	/// <summary>Разбирает accessor: параметры (только у set), затем тело в одной из форм.</summary>
	private PropertyAccessorNode ParsePropertyAccessor(AccessorKind kind, Token accessorToken)
	{
		var parameters = Current.Kind == TokenKind.LeftParen ? ParseParameters() : [];
		if (parameters.Count > 1)
			Report("PAR019", "Accessor set принимает не более одного параметра", accessorToken.Span);
		else if (parameters.Count > 0 && kind != AccessorKind.Set)
			Report("PAR019", "Параметры допустимы только у accessor'а set", accessorToken.Span);
		var body = ParseAccessorBody();
		var end = body?.Span.End ?? Previous.Span.End;
		return new PropertyAccessorNode(kind, body,
			new TextSpan(accessorToken.Span.Start, end - accessorToken.Span.Start))
		{
			Parameters = parameters
		};
	}

	/// <summary>Тело accessor'а: <c>{ ... }</c>, <c>=&gt; expr</c>, выражение/присваивание или пусто.</summary>
	private SyntaxNode? ParseAccessorBody()
	{
		if (Current.Kind == TokenKind.LeftBrace)
			return ParseBlock();
		if (Current.Kind == TokenKind.Operator && Current.Text == "=>")
		{
			Advance();
			return ParseExpression();
		}
		if (Current.Kind is TokenKind.Semicolon or TokenKind.RightBrace || StartsAccessor())
			return null;
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

	private bool StartsAccessor()
	{
		if (Current.Kind != TokenKind.Identifier || Current.Text is not ("get" or "set" or "init"))
			return false;
		if (_position + 1 >= _tokens.Count)
			return true;
		var next = _tokens[_position + 1];
		if (next.Kind is TokenKind.Semicolon or TokenKind.RightBrace or TokenKind.LeftParen or TokenKind.LeftBrace)
			return true;
		if (next.Kind == TokenKind.Operator && next.Text == "=>")
			return true;
		return next.Kind == TokenKind.Identifier && next.Text is "get" or "set" or "init";
	}

	private ConstructorMemberNode ParseConstructor(
		IReadOnlyList<ModifierToken> modifierTokens,
		IReadOnlyList<AttributeNode> attributes)
	{
		var modifiers = BuildModifiers(modifierTokens, ModifierTarget.Constructor, Accessibility.Private);
		var start = Consume(TokenKind.Ctor, "Ожидалось ключевое слово ctor").Span.Start;
		var parameters = ParseParameters();
		ConstructorChainNode? chain = null;
		if (Match(TokenKind.Colon))
			chain = ParseConstructorChain();
		SyntaxNode? body = Current.Kind == TokenKind.LeftBrace ? ParseBlock() : null;
		return new ConstructorMemberNode(parameters, chain, body,
			new TextSpan(start, (body?.Span.End ?? Previous.Span.End) - start),
			modifiers.Accessibility, modifiers.IsOverride, Attributes: attributes);
	}

	/// <summary>Цепочка конструкторов: только <c>: $(args)</c> (spec §16).</summary>
	private ConstructorChainNode? ParseConstructorChain()
	{
		var start = Current.Span.Start;
		if (Current.Kind != TokenKind.Dollar)
		{
			Report("PAR018", "Цепочка конструктора должна иметь вид ': $(...)'", Current.Span);
			SkipUnexpectedConstructorChain();
			return null;
		}
		Advance();
		if (Current.Kind != TokenKind.LeftParen)
		{
			Report("PAR018", "После $ в цепочке конструктора ожидалась '('", Current.Span);
			return null;
		}
		Advance();
		var arguments = new List<ExpressionNode>();
		while (!AtEnd && Current.Kind != TokenKind.RightParen)
		{
			arguments.Add(ParseExpression());
			if (!Match(TokenKind.Comma))
				break;
		}
		var close = Consume(TokenKind.RightParen, "Ожидалась закрывающая скобка цепочки конструктора");
		return new ConstructorChainNode(arguments, new TextSpan(start, close.Span.End - start));
	}

	private void SkipUnexpectedConstructorChain()
	{
		// `: this(...)`/`: A(...)` не поддерживаются: пропускаем до тела или конца объявления.
		while (!AtEnd && Current.Kind is not (TokenKind.LeftBrace or TokenKind.RightBrace or TokenKind.Semicolon))
			Advance();
	}

	private OperatorMemberNode ParseOperatorMember(
		IReadOnlyList<ModifierToken> modifierTokens,
		IReadOnlyList<AttributeNode> attributes)
	{
		var modifiers = BuildModifiers(modifierTokens, ModifierTarget.Operator, Accessibility.Private);
		var start = Consume(TokenKind.Op, "Ожидалось ключевое слово op").Span.Start;
		// B6b1: `op hash()`/`op compare(...)` — это обычные имена операторов, а не владелец
		// расширения. Владелец читается только в форме `op i32.foo` (после имени стоит точка).
		var owner = Current.Kind == TokenKind.Identifier &&
			_position + 1 < _tokens.Count &&
			_tokens[_position + 1].Kind == TokenKind.Dot
				? Advance().Text
				: null;
		if (owner is not null)
			Advance();
		var operatorName = ParseOperatorSymbol();
		var parameters = ParseParameters();
		TypeSyntax? returnType = null;
		if (Current.Kind == TokenKind.Operator && Current.Text == "->")
		{
			Advance();
			returnType = ParseTypeSyntax();
		}
		SyntaxNode? body = null;
		if (Current.Kind == TokenKind.LeftBrace)
		{
			body = ParseBlock();
		}
		else
		{
			// Тело может быть на следующей строке (`op *(…)-> T` + `T(...)`), поэтому
			// отделяем его от начала следующего объявления, а не только по переводу строки.
			var startsDeclaration = Current.Kind is TokenKind.Fun or TokenKind.Type or TokenKind.Unit
				or TokenKind.Op or TokenKind.Ctor or TokenKind.Use or TokenKind.Hash
				or TokenKind.RightBrace or TokenKind.Semicolon;
			if (!startsDeclaration && (!HasNewlineBeforeCurrent() || !modifiers.IsAbstract))
				body = ParseExpression();
		}
		return new OperatorMemberNode(owner is null ? operatorName : $"{owner}.{operatorName}", parameters, returnType, body,
			new TextSpan(start, (body?.Span.End ?? Previous.Span.End) - start),
			modifiers.Accessibility, modifiers.IsOverride, Attributes: attributes);
	}

	/// <summary>
	/// Символ оператора после <c>op</c>. Кроме обычного токена оператора/имени поддержан
	/// индексатор <c>op [](...)</c> (B5a): пустые скобки — его каноническое имя <c>"[]"</c>.
	/// </summary>
	private string ParseOperatorSymbol()
	{
		if (Current.Kind == TokenKind.LeftBracket &&
			_position + 1 < _tokens.Count &&
			_tokens[_position + 1].Kind == TokenKind.RightBracket)
		{
			Advance();
			Advance();
			// `op []=` — сеттер индексатора (spec §5).
			if (Current.Kind == TokenKind.Equals)
			{
				Advance();
				return "[]=";
			}
			return "[]";
		}

		var symbol = Current.Kind is TokenKind.Operator or TokenKind.In
			? Advance()
			: Consume(TokenKind.Identifier, "Ожидался символ оператора");
		return symbol.Text;
	}
}
