using System.Text.RegularExpressions;
using Kern.Compiler.Formatting;
using Kern.Compiler.Lexing;
using Kern.Compiler.Syntax;
using Kern.Lsp.Analysis;
using Kern.Lsp.Protocol;

namespace Kern.Lsp.Features;

/// <summary>
/// Возможности LSP: completion, hover, definition, signature help, symbols и форматирование.
/// Completion/signature help используют семантический снимок <see cref="LspIndex"/>;
/// hover/definition остаются синтаксическими, если типа нет в индексе.
/// </summary>
public static class LspFeatures
{
	private static readonly string[] Keywords =
	[
		"fun", "type", "unit", "use", "loop", "match", "async", "await", "op", "ctor",
		"open", "in", "is", "var", "else", "ret", "stop", "next", "yield", "project",
	];

	public static IReadOnlyList<object> Completion(string text, int offset = 0, LspIndex? index = null)
	{
		string prefix = text[..System.Math.Clamp(offset, 0, text.Length)];
		var items = new List<object>(Keywords.Length + 64);
		var seen = new HashSet<string>(StringComparer.Ordinal);

		void Add(string label, int kind, string detail)
		{
			if (seen.Add(label))
				items.Add(new { label, kind, detail });
		}

		// `use module[` — символы проекта/модуля.
		if (Regex.IsMatch(prefix, @"\buse\s+[A-Za-z0-9_.]+\s*\[[^\]]*$"))
		{
			AddSymbols(Add, index);
			return items;
		}

		// `use ` — имена модулей.
		if (Regex.IsMatch(prefix, @"\buse\s+[A-Za-z0-9_.]*$"))
		{
			if (index is not null)
				foreach (string module in index.Modules)
					Add(module, 9, "модуль");
			return items;
		}

		// `obj.` — члены типа, если удалось вывести тип получателя.
		Match member = Regex.Match(
			prefix, @"(?m)(?<receiver>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*[A-Za-z0-9_]*$");
		if (member.Success && index is not null)
		{
			string receiver = member.Groups["receiver"].Value;
			string? typeName = index.FindType(receiver)?.Name
				?? ReceiverType(text, receiver, index)
				?? (index.MembersForName(receiver).Any() ? receiver : null);
			if (typeName is not null)
			{
				foreach (MemberItem memberItem in index.MembersForName(typeName))
					Add(memberItem.Name, memberItem.Kind switch
					{
						"property" => 10,
						"field" => 5,
						"constructor" => 4,
						_ => 2,
					}, memberItem.Detail);
				if (items.Count > 0)
					return items;
			}
		}

		foreach (string keyword in Keywords)
			Add(keyword, 14, "ключевое слово");

		if (index is not null)
		{
			foreach (var (name, functions) in index.Functions.GroupBy(function => function.Name)
				.Select(group => (Name: group.Key, Functions: group.ToList())))
				foreach (var function in functions)
					Add(name, function.Owner is null ? 3 : 2, function.Signature);

			foreach (var (name, type) in index.Types)
				Add(name, type.Kind is Kern.Compiler.Semantics.Types.SymbolKind.Unit or
					Kern.Compiler.Semantics.Types.SymbolKind.ImmutableUnit ? 23 : 7,
					type.Kind is Kern.Compiler.Semantics.Types.SymbolKind.Unit or
						Kern.Compiler.Semantics.Types.SymbolKind.ImmutableUnit ? "unit" : "type");

			foreach (var global in index.Globals)
				Add(global.Name, 6, $"{global.Type} {global.Name}");
		}

		foreach (Token token in Lexer.Lex(text).Tokens)
		{
			if (token.Kind == TokenKind.Identifier)
				Add(token.Text, 6, "идентификатор");
			if (items.Count >= 800)
				break;
		}

		return items;
	}

	/// <summary>Signature help: сигнатуры перегрузок для вызова, внутри которого стоит курсор.</summary>
	public static object? SignatureHelp(string text, int offset, LspIndex? index)
	{
		if (index is null)
			return null;
		string prefix = text[..System.Math.Clamp(offset, 0, text.Length)];
		IReadOnlyList<Token> tokens = [.. Lexer.Lex(prefix).Tokens
			.Where(token => token.Kind != TokenKind.EndOfFile)];

		int open = -1;
		int depth = 0;
		for (int position = tokens.Count - 1; position >= 0; position--)
		{
			string token = tokens[position].Text;
			if (token == ")") depth++;
			else if (token == "(")
			{
				if (depth == 0)
				{
					open = position;
					break;
				}
				depth--;
			}
		}
		if (open < 1)
			return null;

		Token nameToken = tokens[open - 1];
		if (nameToken.Kind != TokenKind.Identifier)
			return null;
		var candidates = index.FindCallables(nameToken.Text).ToList();
		if (candidates.Count == 0)
			return null;

		int active = 0;
		depth = 0;
		for (int position = open + 1; position < tokens.Count; position++)
		{
			string token = tokens[position].Text;
			if (token is "(" or "[" or "{") depth++;
			else if (token is ")" or "]" or "}") depth--;
			else if (token == "," && depth == 0) active++;
		}

		var signatures = candidates.Take(20).Select(function => new
		{
			label = function.Signature,
			parameters = function.Parameters.Select(parameter => new
			{
				label = $"{parameter.Type} {parameter.Name}",
			}).ToArray(),
		}).ToArray();

		return new
		{
			signatures,
			activeSignature = 0,
			activeParameter = active,
		};
	}

	public static object? Hover(string text, int offset, LspIndex? index = null)
	{
		Token? token = TokenAt(text, offset);
		if (token is not { } found)
			return null;

		var map = new TextMap(text);
		if (index is not null)
		{
			var functions = index.FindCallables(found.Text).Take(3).ToList();
			if (functions.Count > 0)
				return new
				{
					contents = new
					{
						kind = "markdown",
						value = "```kern\n" + string.Join("\n", functions.Select(function => function.Signature)) + "\n```",
					},
					range = map.Range(found.Span.Start, found.Span.Length),
				};

			if (index.FindType(found.Text) is { } type)
			{
				string kind = type.Kind is Kern.Compiler.Semantics.Types.SymbolKind.Unit or
					Kern.Compiler.Semantics.Types.SymbolKind.ImmutableUnit ? "unit" : "type";
				string generic = type.GenericParameters.Count == 0
					? ""
					: "<" + string.Join(", ", type.GenericParameters.Select(parameter => parameter.Name)) + ">";
				return new
				{
					contents = new
					{
						kind = "markdown",
						value = $"```kern\n{kind} {type.Name}{generic}\n```",
					},
					range = map.Range(found.Span.Start, found.Span.Length),
				};
			}
		}

		string line = LineAt(text, found.Span.Start).Trim();
		return new
		{
			contents = new
			{
				kind = "markdown",
				value = $"```kern\n{line}\n```",
			},
			range = map.Range(found.Span.Start, found.Span.Length),
		};
	}

	public static object? Definition(string uri, string text, int offset)
	{
		Token? token = TokenAt(text, offset);
		if (token is not { } found)
			return null;
		IReadOnlyList<Token> tokens = Lexer.Lex(text).Tokens;
		for (var index = 0; index < tokens.Count; index++)
		{
			if (tokens[index].Kind != TokenKind.Identifier ||
				!string.Equals(tokens[index].Text, found.Text, StringComparison.Ordinal))
				continue;
			if (!IsDeclarationName(tokens, index))
				continue;
			var map = new TextMap(text);
			return new
			{
				uri,
				range = map.Range(tokens[index].Span.Start, tokens[index].Span.Length),
			};
		}

		return null;
	}

	public static IReadOnlyList<object> Symbols(string text)
	{
		var map = new TextMap(text);
		var symbols = new List<object>();
		foreach (DeclarationNode declaration in Parser.Parse(text).Tree.Declarations)
		{
			string? name = declaration switch
			{
				FunctionDeclaration function => function.Name,
				TypeDeclaration type => type.Name,
				UnitDeclaration unit => unit.Name,
				GlobalFieldDeclaration field => field.Name,
				_ => null,
			};
			if (string.IsNullOrWhiteSpace(name))
				continue;

			int kind = declaration switch
			{
				FunctionDeclaration => 12,
				TypeDeclaration => 5,
				UnitDeclaration => 23,
				_ => 13,
			};
			object range = map.Range(declaration.Span.Start, declaration.Span.Length);
			symbols.Add(new { name, kind, range, selectionRange = range });
		}

		return symbols;
	}

	public static IReadOnlyList<object> Formatting(string text)
	{
		string formatted = SourceFormatter.Format(text);
		if (string.Equals(formatted, text, StringComparison.Ordinal))
			return [];

		var map = new TextMap(text);
		var end = map.Position(text.Length);
		return
		[
			new
			{
				range = new
				{
					start = new { line = 0, character = 0 },
					end = new { line = end.Line, character = end.Character },
				},
				newText = formatted,
			},
		];
	}

	private static void AddSymbols(Action<string, int, string> add, LspIndex? index)
	{
		if (index is null)
			return;
		foreach (var function in index.Functions)
			add(function.Name, function.Owner is null ? 3 : 2, function.Signature);
		foreach (var (name, type) in index.Types)
			add(name, 7, type.Kind.ToString().ToLowerInvariant());
		foreach (var global in index.Globals)
			add(global.Name, 6, $"{global.Type} {global.Name}");
	}

	/// <summary>Простой вывод типа локальной переменной/параметра по тексту документа.</summary>
	private static string? ReceiverType(string text, string name, LspIndex index)
	{
		string escaped = Regex.Escape(name);
		Match declaration = Regex.Match(text,
			$@"(?m)\b(?<type>[A-Za-z_][A-Za-z0-9_]*(?:<[^>\n]+>)?[?!]?)\s+{escaped}\s*(?=[=,);]|\r?$)",
			RegexOptions.Multiline);
		if (declaration.Success)
		{
			string type = BaseName(declaration.Groups["type"].Value);
			if (index.MembersForName(type).Any())
				return type;
		}

		Match constructed = Regex.Match(text,
			$@"(?m)\b(?:var\s+)?{escaped}\s*=\s*(?<type>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
			RegexOptions.Multiline);
		if (constructed.Success)
		{
			string type = constructed.Groups["type"].Value;
			if (index.MembersForName(type).Any())
				return type;
		}

		return null;
	}

	private static string BaseName(string typeText)
	{
		int angle = typeText.IndexOf('<', StringComparison.Ordinal);
		string result = angle < 0 ? typeText : typeText[..angle];
		return result.TrimEnd('?', '!');
	}

	private static Token? TokenAt(string text, int offset)
	{
		foreach (Token token in Lexer.Lex(text).Tokens)
		{
			if (token.Kind == TokenKind.EndOfFile)
				break;
			if (token.Span.Start <= offset && offset <= token.Span.End)
				return token;
		}

		return null;
	}

	private static bool IsDeclarationName(IReadOnlyList<Token> tokens, int index)
	{
		if (index > 0 && tokens[index - 1].Text is "fun" or "type" or "unit")
			return true;
		// `fun Type.name` — имя метода в выборе родителя/типа.
		return index > 2 && tokens[index - 1].Text == "." && tokens[index - 3].Text == "fun";
	}

	private static string LineAt(string text, int offset)
	{
		int start = text.LastIndexOf('\n', System.Math.Clamp(offset, 0, System.Math.Max(0, text.Length - 1)));
		start = start < 0 ? 0 : start + 1;
		int end = text.IndexOf('\n', start);
		return end < 0 ? text[start..] : text[start..end];
	}
}
