using Kern.Compiler.Lexing;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Syntax;

public sealed partial class Parser
{
	private readonly List<Token> _tokens;
	private readonly string _source;
	private readonly List<Diagnostic> _diagnostics = [];
	private int _position;

	// Приоритеты операторов — ровно как в C# (spec §3), от низкого к высокому.
	// `**` — особый: правоассоциативный и выше унарного минуса.
	private static readonly Dictionary<string, int> Precedence = new(StringComparer.Ordinal)
	{
		["||"] = 1,
		["?:"] = 1,
		["&&"] = 2,
		["|"] = 3,
		["^"] = 4,
		["&"] = 5,
		["=="] = 6, ["!="] = 6, ["==="] = 6, ["!=="] = 6,
		["<"] = 7, [">"] = 7, ["<="] = 7, [">="] = 7, ["in"] = 7, ["!in"] = 7, ["is"] = 7, ["!is"] = 7,
		["<<"] = 8, [">>"] = 8,
		["+"] = 9, ["-"] = 9,
		["*"] = 10, ["/"] = 10, ["%"] = 10,
		["**"] = 11
	};

	private Parser(IReadOnlyList<Token> tokens, string source = "")
	{
		// Копия нужна, чтобы разбор типа мог расщепить `>>` на два `>` (закрытие generic).
		_tokens = [.. tokens];
		_source = source;
	}

	public static (CompilationUnit Tree, IReadOnlyList<Diagnostic> Diagnostics) Parse(string source)
	{
		ArgumentNullException.ThrowIfNull(source);
		var lexed = Lexer.Lex(source);
		var parser = new Parser(lexed.Tokens, source);
		var tree = parser.ParseCompilationUnit();
		return (tree, [.. lexed.Diagnostics, .. parser._diagnostics]);
	}

	public static (ExpressionNode Expression, IReadOnlyList<Diagnostic> Diagnostics) ParseExpression(string source)
	{
		ArgumentNullException.ThrowIfNull(source);
		var lexed = Lexer.Lex(source);
		var parser = new Parser(lexed.Tokens, source);
		var expression = parser.ParseExpression();
		if (!parser.AtEnd)
			parser.Report("PAR004", "После выражения ожидался конец файла");
		return (expression, [.. lexed.Diagnostics, .. parser._diagnostics]);
	}

	public static (CompilationUnit Tree, IReadOnlyList<Diagnostic> Diagnostics) Parse(
		IReadOnlyList<Token> tokens)
	{
		ArgumentNullException.ThrowIfNull(tokens);
		var parser = new Parser(tokens);
		return (parser.ParseCompilationUnit(), parser._diagnostics);
	}

	private Token Current => _tokens[Math.Min(_position, _tokens.Count - 1)];

	private Token Previous => _tokens[Math.Max(0, _position - 1)];

	private bool AtEnd => Current.Kind == TokenKind.EndOfFile;

	/// <summary>
	/// Вклад токена в глубину generic-списка для lookahead-проверок: `&gt;` закрывает один
	/// уровень, `&gt;&gt;` — сразу два (лексер отдаёт сдвиг одним токеном).
	/// </summary>
	private static int GenericDepthDelta(Token token) =>
		token.Kind == TokenKind.Operator
			? token.Text switch { "<" => 1, ">" => -1, ">>" => -2, _ => 0 }
			: 0;

	private CompilationUnit ParseCompilationUnit()
	{
		var declarations = new List<DeclarationNode>();
		var start = Current.Span.Start;
		string? project = null;
		if (Current.Kind == TokenKind.Project)
		{
			var projectStart = Advance().Span.Start;
			var parts = new List<string>
			{
				Consume(TokenKind.Identifier, "Ожидалось имя проекта").Text
			};
			while (Match(TokenKind.Dot))
				parts.Add(Consume(TokenKind.Identifier, "Ожидалось имя после точки в имени проекта").Text);
			Consume(TokenKind.Colon, "Ожидалось ':' после имени проекта");
			project = string.Join('.', parts);
			start = projectStart;
		}
		while (!AtEnd)
		{
			var attributes = ParseAttributes();
			var modifiers = ParseModifierTokens();
			if (Current.Kind == TokenKind.Fun)
				declarations.Add(ParseFunction(attributes,
					BuildModifiers(modifiers, ModifierTarget.TopLevelFunction, Accessibility.Public)));
			else if (Current.Kind == TokenKind.Type)
				declarations.Add(ParseType(attributes,
					BuildModifiers(modifiers, ModifierTarget.TopLevelType, Accessibility.Public)));
			else if (Current.Kind == TokenKind.Unit)
				declarations.Add(ParseUnit(attributes, BuildModifiers(modifiers, ModifierTarget.Unit, Accessibility.Public)));
			else if (Current.Kind == TokenKind.Use)
			{
				BuildModifiers(modifiers, ModifierTarget.Use, Accessibility.Public);
				declarations.Add(ParseUse());
			}
			else if (Current.Kind == TokenKind.Op)
				declarations.Add(ParseExtensionOperator(attributes,
					BuildModifiers(modifiers, ModifierTarget.Extension, Accessibility.Public)));
			else if (Current.Kind == TokenKind.Identifier && LooksLikeExtensionProperty())
				declarations.Add(ParseExtensionProperty(attributes,
					BuildModifiers(modifiers, ModifierTarget.Extension, Accessibility.Public)));
			else if (Current.Kind == TokenKind.Operator && Current.Text == "!" ||
				Current.Kind == TokenKind.Var ||
				Current.Kind == TokenKind.Identifier && LooksLikeGlobalField())
				declarations.Add(ParseGlobalField(attributes,
					BuildModifiers(modifiers, ModifierTarget.GlobalField, Accessibility.Public)));
			else
			{
				if (modifiers.Count > 0)
					Report("PAR017", "Модификаторы допустимы только перед объявлением", modifiers[0].Span);
				Report("PAR001", "Ожидалось объявление функции, типа, unit или use");
				RecoverDeclaration();
			}
		}
		return new CompilationUnit(declarations, new TextSpan(start, Current.Span.End - start)) { Project = project };
	}

	private ExtensionOperatorDeclaration ParseExtensionOperator(
		IReadOnlyList<AttributeNode> attributes,
		DeclarationModifiers modifiers)
	{
		var start = Consume(TokenKind.Op, "Ожидалось ключевое слово op").Span.Start;
		var receiver = ParseTypeSyntax();
		Consume(TokenKind.Dot, "Ожидалась точка перед оператором");
		var op = Current.Kind == TokenKind.Operator ? Advance() : Consume(TokenKind.Identifier, "Ожидался оператор");
		var parameters = ParseParameters();
		TypeSyntax? returnType = null;
		if (Current.Kind == TokenKind.Operator && Current.Text == "->")
		{
			Advance();
			returnType = ParseTypeSyntax();
		}
		SyntaxNode? body = Current.Kind == TokenKind.LeftBrace ? ParseBlock() : ParseFunctionBody();
		return new ExtensionOperatorDeclaration(receiver, op.Text, parameters, returnType, body,
			new TextSpan(start, (body?.Span.End ?? Previous.Span.End) - start), modifiers.Accessibility,
			Attributes: attributes);
	}

	private bool LooksLikeGlobalField()
	{
		var index = _position;
		if (_tokens[index].Kind == TokenKind.Operator && _tokens[index].Text == "!") index++;
		if (index < _tokens.Count && _tokens[index].Kind == TokenKind.Var)
			return index + 2 < _tokens.Count && _tokens[index + 1].Kind == TokenKind.Operator &&
				_tokens[index + 1].Text == "!" && _tokens[index + 2].Kind == TokenKind.Identifier;
		if (index >= _tokens.Count || _tokens[index].Kind != TokenKind.Identifier) return false;
		index++;
		if (index < _tokens.Count && _tokens[index].Kind == TokenKind.Operator && _tokens[index].Text == "<")
		{
			var depth = 0;
			do
			{
				depth += GenericDepthDelta(_tokens[index]);
				index++;
			} while (index < _tokens.Count && depth > 0);
		}
		while (index < _tokens.Count && _tokens[index].Kind == TokenKind.Operator && _tokens[index].Text is "?" or "!") index++;
		return index < _tokens.Count && _tokens[index].Kind == TokenKind.Identifier;
	}

	private GlobalFieldDeclaration ParseGlobalField(
		IReadOnlyList<AttributeNode> attributes,
		DeclarationModifiers modifiers)
	{
		var start = Current.Span.Start;
		var isRequired = Current.Kind == TokenKind.Operator && Current.Text == "!";
		if (isRequired) Advance();
		var isInferred = Current.Kind == TokenKind.Var;
		Token? inferredToken = null;
		if (isInferred)
		{
			inferredToken = Advance();
			if (!(Current.Kind == TokenKind.Operator && Current.Text == "!"))
				Report("PAR010", "Ожидался ! после var");
			else
				Advance();
		}
		var type = isInferred
			? NamedTypeSyntax.Plain("var", inferredToken!.Value.Span)
			: ParseTypeSyntax();
		// Маркер `!` остаётся в синтаксисе типа, IsRequired сохраняет прежнее значение.
		if (isInferred || type is NamedTypeSyntax { IsSingleAssignment: true })
			isRequired = true;
		var name = Consume(TokenKind.Identifier, "Ожидалось имя глобального поля");
		var initializer = Match(TokenKind.Equals) ? ParseExpression() : null;
		if (isInferred && initializer is null)
			Report("PAR010", "Объявление var! требует начального значения");
		return new GlobalFieldDeclaration(type, name.Text, isRequired, initializer,
			new TextSpan(start, (initializer?.Span.End ?? name.Span.End) - start), isInferred, modifiers.Accessibility,
			Attributes: attributes);
	}

	private bool LooksLikeGenericExtensionFunction()
	{
		var index = _position;
		var depth = 0;
		for (; index < _tokens.Count; index++)
		{
			var token = _tokens[index];
			if (token.Kind == TokenKind.Operator && token.Text == "<")
			{
				depth++;
				continue;
			}
			if (token.Kind == TokenKind.Operator && token.Text is ">" or ">>")
			{
				depth -= token.Text == ">>" ? 2 : 1;
				if (depth <= 0)
				{
					index++;
					break;
				}
				continue;
			}
			if (token.Kind is TokenKind.Semicolon or TokenKind.LeftBrace or TokenKind.RightBrace or TokenKind.Equals)
				return false;
		}

		return index < _tokens.Count && _tokens[index].Kind == TokenKind.Dot;
	}

	private bool LooksLikeExtensionProperty()
	{
		var index = _position + 1;
		if (index >= _tokens.Count || _tokens[index].Kind != TokenKind.Identifier)
			return false;
		index++;
		// Приёмник с generic-аргументами: `i32 list<i32>.count …` — `>` может прийти как `>>`.
		if (index < _tokens.Count && _tokens[index].Kind == TokenKind.Operator && _tokens[index].Text == "<")
		{
			var depth = 0;
			for (; index < _tokens.Count; index++)
			{
				var token = _tokens[index];
				if (token.Kind == TokenKind.Operator && token.Text == "<")
				{
					depth++;
					continue;
				}
				if (token.Kind == TokenKind.Operator && token.Text is ">" or ">>")
				{
					depth -= token.Text == ">>" ? 2 : 1;
					if (depth <= 0)
					{
						index++;
						break;
					}
					continue;
				}
				if (token.Kind is TokenKind.Semicolon or TokenKind.LeftBrace or TokenKind.RightBrace)
					return false;
			}
		}

		return index + 1 < _tokens.Count &&
			_tokens[index].Kind == TokenKind.Dot &&
			_tokens[index + 1].Kind == TokenKind.Identifier;
	}

	private ExtensionPropertyDeclaration ParseExtensionProperty(
		IReadOnlyList<AttributeNode> attributes,
		DeclarationModifiers modifiers)
	{
		var start = Current.Span.Start;
		var propertyType = ParseTypeSyntax();
		var receiver = ParseTypeSyntax();
		Consume(TokenKind.Dot, "Ожидалась точка перед именем extension property");
		var name = Consume(TokenKind.Identifier, "Ожидалось имя extension property");
		var body = ParseExpression();
		return new ExtensionPropertyDeclaration(receiver, name.Text, propertyType, body,
			new TextSpan(start, body.Span.End - start), modifiers.Accessibility, Attributes: attributes);
	}

	private IReadOnlyList<AttributeNode> ParseAttributes()
	{
		var attributes = new List<AttributeNode>();
		while (Current.Kind == TokenKind.Hash && _position + 1 < _tokens.Count && _tokens[_position + 1].Kind == TokenKind.LeftBracket)
		{
			var groupStart = Advance().Span.Start;
			Consume(TokenKind.LeftBracket, "Ожидалась открывающая скобка атрибута");
			var first = true;
			while (true)
			{
				var itemStart = first ? groupStart : Current.Span.Start;
				first = false;
				var name = Consume(TokenKind.Identifier, "Ожидалось имя атрибута");
				var arguments = new List<string>();
				if (Match(TokenKind.LeftParen))
				{
					while (!AtEnd && Current.Kind != TokenKind.RightParen)
					{
						arguments.Add(Advance().Text);
						if (!Match(TokenKind.Comma)) break;
					}
					Consume(TokenKind.RightParen, "Ожидалась закрывающая скобка атрибута");
				}
				var itemEnd = Previous.Span.End;
				attributes.Add(new AttributeNode(name.Text, arguments, new TextSpan(itemStart, itemEnd - itemStart)));
				if (!Match(TokenKind.Comma))
					break;
			}
			Consume(TokenKind.RightBracket, "Ожидалась закрывающая скобка атрибута");
		}
		return attributes;
	}

	private TypeDeclaration ParseType(IReadOnlyList<AttributeNode> attributes, DeclarationModifiers? modifiers = null)
	{
		var start = Consume(TokenKind.Type, "Ожидалось ключевое слово type").Span.Start;
		var name = Consume(TokenKind.Identifier, "Ожидалось имя типа");
		var isAbstract = modifiers?.IsAbstract ?? false;
		var isOpen = modifiers?.IsOpen ?? false;
		var isStatic = modifiers?.IsStatic ?? false;
		var accessibility = modifiers?.Accessibility ?? Accessibility.Public;
		if (Match(TokenKind.Equals))
		{
			var union = ParseUnionTypeSyntax();
			return new TypeDeclaration(name.Text, new TextSpan(start, Previous.Span.End - start),
				Attributes: attributes, IsAbstract: isAbstract, IsOpen: isOpen, UnionVariants: union.Alternatives,
				IsStatic: isStatic, Accessibility: accessibility);
		}
		var genericParameters = IsOperator("<") ? ParseGenericParameterList() : [];
		var primaryParameters = Current.Kind == TokenKind.LeftParen ? ParseParameters() : [];
		var parents = Current.Kind == TokenKind.LeftBracket ? ParseParentList() : [];
		var members = ParseTypeMembers();
		var end = Previous.Span.End;
		return new TypeDeclaration(name.Text, new TextSpan(start, end - start), members, attributes, isAbstract, isOpen,
			IsStatic: isStatic, Accessibility: accessibility)
		{
			GenericParameters = genericParameters,
			PrimaryParameters = primaryParameters,
			Parents = parents
		};
	}

	private UnitDeclaration ParseUnit(IReadOnlyList<AttributeNode> attributes, DeclarationModifiers modifiers)
	{
		var start = Consume(TokenKind.Unit, "Ожидалось ключевое слово unit").Span.Start;
		var isImmutable = Current.Kind == TokenKind.Operator && Current.Text == "!";
		if (isImmutable)
			Advance();
		var name = Consume(TokenKind.Identifier, "Ожидалось имя unit");
		var genericParameters = IsOperator("<") ? ParseGenericParameterList() : [];
		var parameters = Current.Kind == TokenKind.LeftParen ? ParseParameters() : [];
		if (Current.Kind == TokenKind.LeftBracket)
		{
			// unit не поддерживает наследование (spec §9): сообщаем и пропускаем список для восстановления.
			Report("PAR013", "unit не поддерживает наследование: список родителей запрещён", Current.Span);
			ParseParentList();
		}
		var members = Current.Kind == TokenKind.LeftBrace ? ParseTypeMembers() : [];
		return new UnitDeclaration(name.Text, new TextSpan(start, Previous.Span.End - start), members,
			parameters, isImmutable, modifiers.Accessibility, attributes)
		{
			GenericParameters = genericParameters
		};
	}

	private FunctionDeclaration ParseFunction(IReadOnlyList<AttributeNode>? attributes = null, DeclarationModifiers? modifiers = null)
	{
		var start = Consume(TokenKind.Fun, "Ожидалось ключевое слово fun").Span.Start;
		var name = ConsumeMemberName("Ожидалось имя функции");
		var functionName = name.Text;
		TypeSyntax? extensionReceiver = null;
		IReadOnlyList<GenericParameterNode> genericParameters = [];
		if (IsOperator("<"))
		{
			// `fun list<i32>.total(...)`: generic-приёмник расширения, а не generic-параметры.
			if (LooksLikeGenericExtensionFunction())
			{
				extensionReceiver = ParseTypeSyntaxFrom(name);
				Consume(TokenKind.Dot, "Ожидалась точка перед именем расширяемого члена");
				var member = Consume(TokenKind.Identifier, "Ожидалось имя расширяемого члена");
				functionName = $"{extensionReceiver}.{member.Text}";
			}
			else
			{
				genericParameters = ParseGenericParameterList();
			}
		}

		if (extensionReceiver is null && Match(TokenKind.Dot))
			functionName = $"{functionName}.{Consume(TokenKind.Identifier, "Ожидалось имя расширяемого члена").Text}";
		var parameters = ParseParameters();
		TypeSyntax? returnType = null;
		if (Current.Kind == TokenKind.Operator && Current.Text == "->")
		{
			Advance();
			returnType = ParseTypeSyntax();
		}
		var isAbstract = modifiers?.IsAbstract ?? false;
		var isStatic = modifiers?.IsStatic ?? false;
		var accessibility = modifiers?.Accessibility ?? Accessibility.Private;
		var isOverride = modifiers?.IsOverride ?? false;
		var isAsync = modifiers?.IsAsync ?? false;
		var isOpen = modifiers?.IsOpen ?? false;
		if (isAbstract && Current.Kind != TokenKind.LeftBrace)
			return new FunctionDeclaration(functionName, parameters, new BlockStatement([], new TextSpan(name.Span.End, 0)), new TextSpan(start, Previous.Span.End - start), returnType, attributes, true, isStatic, accessibility, isOverride, isAsync, isOpen, ExtensionReceiver: extensionReceiver)
			{
				GenericParameters = genericParameters
			};
		var body = ParseFunctionBody();
		return new FunctionDeclaration(functionName, parameters, body, new TextSpan(start, body.Span.End - start), returnType, attributes, isAbstract, isStatic, accessibility, isOverride, isAsync, isOpen, ExtensionReceiver: extensionReceiver)
		{
			GenericParameters = genericParameters
		};
	}

	private BlockStatement ParseFunctionBody()
	{
		if (Current.Kind == TokenKind.LeftBrace)
			return ParseBlock();
		var expression = ParseExpression();
		return new BlockStatement([new ExpressionStatement(expression, expression.Span)], expression.Span);
	}

	private UseDeclaration ParseUse()
	{
		var start = Consume(TokenKind.Use, "Ожидалось ключевое слово use").Span.Start;
		var parts = new List<string>();
		var name = Consume(TokenKind.Identifier, "Ожидалось имя модуля");
		parts.Add(name.Text);
		var wildcard = false;
		while (Match(TokenKind.Dot))
		{
			if (IsOperator("*"))
			{
				Advance();
				wildcard = true;
				break;
			}
			var part = Consume(TokenKind.Identifier, "Ожидалось имя после точки");
			parts.Add(part.Text);
		}

		var items = new List<string>();
		if (!wildcard && Match(TokenKind.LeftBracket))
		{
			if (Current.Kind != TokenKind.RightBracket)
			{
				do
				{
					items.Add(Consume(TokenKind.Identifier, "Ожидалось имя импортируемого символа").Text);
				}
				while (Match(TokenKind.Comma));
			}
			Consume(TokenKind.RightBracket, "Ожидалась ] после списка импортируемых символов");
		}

		return new UseDeclaration(string.Join('.', parts), new TextSpan(start, Previous.Span.End - start), items, wildcard);
	}


	private void SkipBalancedParentheses()
	{
		var depth = 0;
		do
		{
			if (Current.Kind == TokenKind.LeftParen) depth++;
			else if (Current.Kind == TokenKind.RightParen) depth--;
			Advance();
		} while (!AtEnd && depth > 0);
	}
	private void SkipBalancedBlock()
	{
		var depth = 0;
		do
		{
			if (Current.Kind == TokenKind.LeftBrace)
				depth++;
			else if (Current.Kind == TokenKind.RightBrace)
				depth--;
			Advance();
		}
		while (!AtEnd && depth > 0);
	}

	private BlockStatement ParseBlock()
	{
		var open = Consume(TokenKind.LeftBrace, "Ожидалось тело функции в фигурных скобках");
		var statements = new List<StatementNode>();
		while (!AtEnd && Current.Kind != TokenKind.RightBrace)
		{
			var before = _position;
			statements.Add(ParseStatement());
			Match(TokenKind.Semicolon);
			if (_position == before)
				Advance();
		}
		var close = Consume(TokenKind.RightBrace, "Ожидалась закрывающая фигурная скобка");
		return new BlockStatement(statements, new TextSpan(open.Span.Start, close.Span.End - open.Span.Start));
	}

	private IfExpression ParseIfExpression(bool requireElse = true)
	{
		var start = Consume(TokenKind.Identifier, "Ожидалось ключевое слово if").Span.Start;
		var condition = ParseExpression();
		SyntaxNode then = Current.Kind == TokenKind.LeftBrace ? ParseBlock() : ParseExpression();
		SyntaxNode? otherwise = null;
		if (Match(TokenKind.Else))
		{
			otherwise = Current.Kind == TokenKind.Identifier && Current.Text == "if"
				? ParseIfExpression(requireElse)
				: Current.Kind == TokenKind.LeftBrace ? ParseBlock() : ParseExpression();
		}
		else if (requireElse)
			Report("PAR007", "Ожидалась ветка else у if-выражения");
		var end = otherwise?.Span.End ?? then.Span.End;
		return new IfExpression(condition, then, otherwise, new TextSpan(start, end - start));
	}

	private StructuralPattern ParseStructuralPattern(Token type)
	{
		var closeKind = Current.Kind == TokenKind.LeftParen ? TokenKind.RightParen : TokenKind.RightBrace;
		Advance();
		var fields = new List<NamedFieldPattern>();
		var restCount = 0;
		while (!AtEnd && Current.Kind != closeKind)
		{
			if (Current.Kind == TokenKind.Operator && Current.Text == "..")
			{
				Advance();
				restCount++;
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
						nested = new BindingPattern(binding.Text, true, new TextSpan(variable.Span.Start, binding.Span.End - variable.Span.Start));
				}
					else
						nested = ParsePattern();
				}
				fields.Add(new NamedFieldPattern(name.Text, nested, new TextSpan(name.Span.Start, (nested?.Span.End ?? name.Span.End) - name.Span.Start)));
			}
			else
			{
				Report("PAR006", "Ожидался именованный член structural pattern");
				Advance();
			}
			if (!Match(TokenKind.Comma)) break;
		}
		var close = Consume(closeKind, "Ожидалась закрывающая скобка structural pattern");
		return new StructuralPattern(NamedTypeSyntax.Plain(type.Text, type.Span), fields, restCount > 0, new TextSpan(type.Span.Start, close.Span.End - type.Span.Start), null, restCount);
	}

}