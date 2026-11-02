using Kern.Compiler.Formatting;
using Kern.Compiler.Lexing;
using Kern.Compiler.Syntax;
using Kern.Lsp.Protocol;

namespace Kern.Lsp.Features;

/// <summary>
/// Синтаксические возможности LSP MVP: completion, hover, definition, symbols и
/// форматирование. Работают по тексту и токенам документа, без типовой информации
/// (диагностика идёт через <c>ProjectCompiler</c>).
/// </summary>
public static class LspFeatures
{
	private static readonly string[] Keywords =
	[
		"fun", "type", "unit", "use", "loop", "match", "async", "await", "op", "ctor",
		"open", "in", "is", "var", "else", "ret", "stop", "next", "yield", "project",
	];

	public static IReadOnlyList<object> Completion(string text)
	{
		var items = new List<object>(Keywords.Length + 32);
		foreach (string keyword in Keywords)
			items.Add(new { label = keyword, kind = 14, detail = "ключевое слово" });

		var seen = new HashSet<string>(Keywords, StringComparer.Ordinal);
		foreach (Token token in Lexer.Lex(text).Tokens)
		{
			if (token.Kind != TokenKind.Identifier || !seen.Add(token.Text))
				continue;
			items.Add(new { label = token.Text, kind = 6, detail = "идентификатор" });
			if (items.Count >= 500)
				break;
		}

		return items;
	}

	public static object? Hover(string text, int offset)
	{
		Token? token = TokenAt(text, offset);
		if (token is not { } found)
			return null;
		string line = LineAt(text, found.Span.Start).Trim();
		var map = new TextMap(text);
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
