using System.Text;
using Kern.Compiler.Lexing;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Project;
using Kern.Core;
using Kern.Runtime.Macros;

namespace Kern.Cli.Macros;

internal sealed partial class MacroEngine
{
	/// <summary>
	/// Макросы-выражения: `имя!(a, b)` заменяется текстом результата в скобках. Аргументы передаются как исходный текст;
	/// вложенные вызовы раскрываются следующими проходами (до <see cref="MaxPasses"/>).
	/// </summary>
	private string ExpandExpressions(SourceText file, Dictionary<string, MacroInfo> macros, DiagnosticBag diagnostics)
	{
		var text = file.Text;
		var names = macros.Values.Where(m => m.IsExpr).Select(m => m.Name).ToHashSet();
		if (names.Count == 0 || !text.Contains("!(")) return text;

		for (int pass = 0; pass < MaxPasses; pass++)
		{
			var sf = new SourceFile(file.Path, text);
			var tokens = Lexer.Lex(sf, new DiagnosticBag());
			var edits = new List<(int Start, int End, string Text)>();
			for (int i = 0; i + 2 < tokens.Count; i++)
			{
				var name = tokens[i];
				if (name.Kind != TokenKind.Ident || !names.Contains(name.Text)) continue;
				if (tokens[i + 1].Kind != TokenKind.Bang || tokens[i + 2].Kind != TokenKind.LParen) continue;
				if (tokens[i + 1].Span.Start != name.Span.End || tokens[i + 2].Span.Start != tokens[i + 1].Span.End) continue;

				int close = MatchClose(tokens, i + 2);
				if (close < 0)
				{
					diagnostics.Error(Krn.MacroError, $"не закрыта скобка в вызове макроса '{name.Text}!'", sf, name.Span);
					continue;
				}
				var span = TextSpan.FromBounds(name.Span.Start, tokens[close].Span.End);
				var ctx = new MacroCtx(name.Text, file.Path, sf.GetLineColumn(name.Span.Start).Line, null, SplitArgs(text, tokens, i + 2, close));
				var returned = Run(macros[name.Text], ctx, sf, span, diagnostics);
				var code = ctx.ExprResult ?? returned;
				edits.Add((span.Start, span.End, code is null ? "nil" : "(" + code + ")"));
				i = close;
			}
			if (edits.Count == 0) return text;

			var sb = new StringBuilder();
			int pos = 0;
			foreach (var (start, end, replacement) in edits)
			{
				sb.Append(text, pos, start - pos).Append(replacement);
				pos = end;
			}
			text = sb.Append(text, pos, text.Length - pos).ToString();
		}
		diagnostics.Error(Krn.MacroRecursion, $"раскрытие макросов не закончилось за {MaxPasses} проходов (рекурсивный макрос?)", new SourceFile(file.Path, text), default);
		return text;
	}

	private static int MatchClose(List<Token> tokens, int open)
	{
		int depth = 0;
		for (int i = open; i < tokens.Count; i++)
			switch (tokens[i].Kind)
			{
				case TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace:
					depth++;
					break;
				case TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace:
					if (--depth == 0) return i;
					break;
				case TokenKind.Eof:
					return -1;
			}
		return -1;
	}

	/// <summary>Аргументы между скобками: разделитель — запятая верхнего уровня; пустой список `()` даёт ноль аргументов.</summary>
	private static List<string> SplitArgs(string text, List<Token> tokens, int open, int close)
	{
		var args = new List<string>();
		int segStart = tokens[open].Span.End, depth = 0;
		for (int i = open + 1; i < close; i++)
		{
			switch (tokens[i].Kind)
			{
				case TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace: depth++; break;
				case TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace: depth--; break;
				case TokenKind.Comma when depth == 0:
					args.Add(Src(text, TextSpan.FromBounds(segStart, tokens[i].Span.Start)).Trim());
					segStart = tokens[i].Span.End;
					break;
			}
		}
		var last = Src(text, TextSpan.FromBounds(segStart, tokens[close].Span.Start)).Trim();
		if (last.Length > 0 || args.Count > 0) args.Add(last);
		return args;
	}
}
