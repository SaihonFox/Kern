using Kern.Compiler.Lexing;
using Kern.Compiler.Syntax;
using Kern.Core;

namespace Kern.Compiler.Parsing;

// ============================================================================
// Парсер Kern: рекурсивный спуск + Pratt для выражений.
//
// Правила переводов строк (токен NewLine значим):
//  * Выражение/оператор заканчивается на NewLine, если оно не продолжается:
//      - бинарным оператором в КОНЦЕ строки (после оператора переводы строк пропускаются);
//      - началом следующей строки с `.` или `?.` (цепочка вызовов).
//    Бинарный оператор в НАЧАЛЕ строки продолжение не образует (`-x` — новое выражение).
//  * Внутри () и [] переводы строк игнорируются (счётчик _ignoreNl); внутри {} блока,
//    тела типа, match — снова значимы (счётчик сохраняется и обнуляется).
//  * Выражения в блоке разделяются NewLine или `;`.
//  * `else` должен стоять на той же строке, что `}` (для ветки без {} допускается и на следующей).
//  * Ветки if/else/loop/match/функций — блок {} ИЛИ одно выражение без скобок.
//  * Тело функции может начинаться на следующей строке после сигнатуры.
//
// Generic-вызов `name<T>(args)`: после идентификатора/члена `<` пробуется спекулятивно как
// список типов; это generic только если за `>` идёт `(` или `[`. Иначе — оператор `<`.
// `>>` в типах — два токена `>` (лексер не склеивает). Ограничение: `>=` сразу после
// закрытия generic (`list<i32>=`) не расщепляется — писать с пробелом.
//
// Объявление vs выражение в блоке: спекулятивно пробуется `Type name ...`; при неудаче —
// обычное выражение.
//
// Восстановление: ошибка «ожидалось X, найдено Y» репортится один раз на позицию (без каскада);
// синхронизация по NewLine/`;`/`}` в блоках и членах, по началу объявления на верхнем уровне.
// ============================================================================

/// <summary>Публичная точка входа для разбора текста.</summary>
public static class ParserFacade
{
	public static CompilationUnit ParseText(string path, string text, string? projectName, DiagnosticBag diags) =>
		Parser.ParseFile(new SourceFile(path, text, projectName), diags);

	/// <summary>Разбор для форматтера: дополнительно возвращает строки (с 0), которые продолжают выражение предыдущей строки.</summary>
	public static CompilationUnit ParseForFormatting(string path, string text, DiagnosticBag diags, out HashSet<int> continuationLines)
	{
		continuationLines = [];
		return Parser.ParseFile(new SourceFile(path, text), diags, continuationLines);
	}
}

public sealed partial class Parser
{
	private readonly SourceFile _file;
	private readonly DiagnosticBag _diags;
	private readonly List<Token> _t;
	private int _p;
	private int _prevEnd;
	private int _ignoreNl;       // >0 — внутри () []: NewLine пропускаются
	private int _spec;           // >0 — спекулятивный разбор: ошибки не репортятся
	private bool _specFailed;
	private int _lastErrPos = -1;
	private bool _typeInExpr;    // тип разбирается после `is`: `?` может быть тернарным
	private int _noRangeStep;    // >0 — `:` не считается шагом диапазона (ветка тернарного оператора)
	private readonly HashSet<int> _continuationLines = [];  // строки (с 0), начинающиеся как продолжение выражения

	private Parser(SourceFile file, DiagnosticBag diags, List<Token> tokens)
	{
		_file = file;
		_diags = diags;
		_t = tokens;
		if (_t.Count == 0 || _t[^1].Kind != TokenKind.Eof)
			_t.Add(new Token(TokenKind.Eof, "", new TextSpan(file.Text.Length, 0)));
	}

	/// <summary>Разбирает файл целиком. Ошибки складываются в <paramref name="diags"/>.</summary>
	public static CompilationUnit ParseFile(SourceFile file, DiagnosticBag diags, HashSet<int>? continuationLines = null)
	{
		var tokens = Lexer.Lex(file, diags);
		var parser = new Parser(file, diags, tokens);
		var unit = parser.ParseCompilationUnit();
		continuationLines?.UnionWith(parser._continuationLines);
		return unit;
	}

	// ---------------- Курсор ----------------

	private void SkipIgnoredNl()
	{
		if (_ignoreNl > 0)
			while (_t[_p].Kind == TokenKind.NewLine) _p++;
	}

	private Token Cur
	{
		get { SkipIgnoredNl(); return _t[_p]; }
	}

	private TokenKind Kind => Cur.Kind;

	private bool At(TokenKind k) => Cur.Kind == k;

	private bool AtEof => Cur.Kind == TokenKind.Eof;

	/// <summary>n-й токен вперёд (с учётом игнорирования NewLine внутри скобок).</summary>
	private Token Peek(int n)
	{
		SkipIgnoredNl();
		int i = _p;
		while (n > 0 && i < _t.Count - 1)
		{
			i++;
			if (_ignoreNl > 0)
				while (_t[i].Kind == TokenKind.NewLine && i < _t.Count - 1) i++;
			n--;
		}
		return _t[i];
	}

	/// <summary>Первый токен после пропуска NewLine (начиная с текущей позиции), позицию не меняет.</summary>
	private Token PeekPastNewLines()
	{
		int i = _p;
		while (_t[i].Kind == TokenKind.NewLine && i < _t.Count - 1) i++;
		return _t[i];
	}

	private Token Advance()
	{
		var t = Cur;
		if (t.Kind != TokenKind.Eof) _p++;
		_prevEnd = t.Span.End;
		return t;
	}

	private bool Eat(TokenKind k)
	{
		if (Cur.Kind != k) return false;
		Advance();
		return true;
	}

	/// <param name="continuation">false — перенос структурный (после атрибутов, `{` с новой строки): строка не считается продолжением.</param>
	private void SkipNewLines(bool continuation = true)
	{
		int before = _p;
		while (_t[_p].Kind == TokenKind.NewLine) _p++;
		if (continuation && _p > before && _ignoreNl == 0 && _spec == 0 && _t[_p].Kind is not (TokenKind.LBrace or TokenKind.RBrace or TokenKind.Eof or TokenKind.KwElse))
			_continuationLines.Add(_file.GetLineColumn(_t[_p].Span.Start).Line - 1);
	}

	/// <summary>Пропуск NewLine и `;` (разделители выражений/членов).</summary>
	private void SkipSeparators()
	{
		while (_t[_p].Kind is TokenKind.NewLine or TokenKind.Semicolon) _p++;
	}

	private bool AtSeparatorOrEnd =>
		Cur.Kind is TokenKind.NewLine or TokenKind.Semicolon or TokenKind.RBrace or TokenKind.Eof;

	private TextSpan SpanFrom(int start) => TextSpan.FromBounds(start, Math.Max(start, _prevEnd));

	private int StartPos => Cur.Span.Start;

	/// <summary>Входит в блок/тело: переводы строк снова значимы. Возвращает сохранённое значение.</summary>
	private int EnterBlock()
	{
		int saved = _ignoreNl;
		_ignoreNl = 0;
		return saved;
	}

	private void ExitBlock(int saved) => _ignoreNl = saved;

	// ---------------- Спекуляция ----------------

	/// <summary>Пробует разобрать; при ошибке или null откатывает курсор и возвращает null.</summary>
	private T? Speculate<T>(Func<T?> f) where T : class
	{
		int p = _p, prev = _prevEnd, nl = _ignoreNl;
		bool inExpr = _typeInExpr;
		int noStep = _noRangeStep;
		bool oldFail = _specFailed;
		_spec++;
		_specFailed = false;
		T? r;
		try { r = f(); }
		finally { _spec--; }
		bool failed = _specFailed;
		_specFailed = oldFail;
		if (r is null || failed)
		{
			_p = p; _prevEnd = prev; _ignoreNl = nl; _typeInExpr = inExpr; _noRangeStep = noStep;
			return null;
		}
		return r;
	}

	private static readonly object Ok = new();

	// ---------------- Ошибки ----------------

	private void Error(TextSpan span, string message)
	{
		if (_spec > 0) { _specFailed = true; return; }
		if (span.Start <= _lastErrPos) return; // без каскада на одной позиции
		_lastErrPos = span.Start;
		_diags.Error("PAR001", message, _file, span);
	}

	private static string Describe(Token t) => t.Kind switch
	{
		TokenKind.Eof => "конец файла",
		TokenKind.NewLine => "конец строки",
		TokenKind.Ident => $"идентификатор '{t.Text}'",
		TokenKind.Number => $"число '{t.Text}'",
		TokenKind.String or TokenKind.FString => "строку",
		TokenKind.Char => "символ",
		_ => $"'{t.Text}'",
	};

	private void ErrorExpected(string what)
	{
		var t = Cur;
		Error(t.Span, $"ожидалось {what}, найдено {Describe(t)}");
	}

	/// <summary>Требует токен; при отсутствии репортит и НЕ потребляет. Возвращает true, если токен был.</summary>
	private bool Expect(TokenKind k, string what)
	{
		if (Cur.Kind == k) { Advance(); return true; }
		ErrorExpected(what);
		return false;
	}

	/// <summary>Идентификатор; при отсутствии — ошибка и имя "&lt;missing&gt;" без потребления токена.</summary>
	private (string Name, TextSpan Span) ExpectIdent(string what = "идентификатор")
	{
		var t = Cur;
		if (t.Kind == TokenKind.Ident)
		{
			Advance();
			return (t.Text, t.Span);
		}
		ErrorExpected(what);
		return ("<missing>", new TextSpan(t.Span.Start, 0));
	}

	/// <summary>Имя члена/метода: допускаются и ключевые слова (`next`, `stop`, …), как после точки — это нужно протоколам `iter`/`async_iter`.</summary>
	private (string Name, TextSpan Span) ExpectMemberName(string what)
	{
		var t = Cur;
		if (t.Kind == TokenKind.Ident || TokenFacts.KeywordText(t.Kind) is not null)
		{
			Advance();
			return (t.Text, t.Span);
		}
		ErrorExpected(what);
		return ("<missing>", new TextSpan(t.Span.Start, 0));
	}

	private static bool IsDeclStart(TokenKind k) => k is
		TokenKind.KwFun or TokenKind.KwType or TokenKind.KwUnit or TokenKind.KwEnum or TokenKind.KwAttribute
		or TokenKind.KwOp or TokenKind.KwUse or TokenKind.HashBracket or TokenKind.KwPub or TokenKind.KwPriv
		or TokenKind.KwProt or TokenKind.KwInt or TokenKind.KwOpen or TokenKind.KwAsync or TokenKind.At;

	/// <summary>Верхний уровень: пропуск до начала следующего объявления (в начале строки).</summary>
	private void SyncToDecl()
	{
		_ignoreNl = 0;
		int depth = 0;
		while (_t[_p].Kind != TokenKind.Eof)
		{
			var k = _t[_p].Kind;
			if (k == TokenKind.LBrace) depth++;
			else if (k == TokenKind.RBrace && depth > 0) depth--;
			_p++;
			_prevEnd = _t[_p - 1].Span.End;
			if (depth == 0 && k == TokenKind.NewLine && IsDeclStart(_t[_p].Kind)) return;
		}
	}

	/// <summary>Внутри блока/тела: пропуск до конца текущей строки/`;` или до закрывающей `}` (не потребляется).</summary>
	private void SyncMember()
	{
		_ignoreNl = 0;
		int depth = 0;
		while (_t[_p].Kind != TokenKind.Eof)
		{
			var k = _t[_p].Kind;
			if (k == TokenKind.LBrace) depth++;
			else if (k == TokenKind.RBrace)
			{
				if (depth == 0) return;
				depth--;
			}
			else if (depth == 0 && k is TokenKind.NewLine or TokenKind.Semicolon)
			{
				_p++;
				return;
			}
			_p++;
			_prevEnd = _t[_p - 1].Span.End;
		}
	}

	// ---------------- Файл ----------------

	private CompilationUnit ParseCompilationUnit()
	{
		int start = 0;
		string? project = null;
		var uses = new List<UseDecl>();
		var decls = new List<Decl>();

		SkipSeparators();
		if (At(TokenKind.KwProject))
		{
			Advance();
			var (name, _) = ExpectIdent("имя проекта");
			var sb = new System.Text.StringBuilder(name);
			while (At(TokenKind.Dot) && Peek(1).Kind == TokenKind.Ident)
			{
				Advance();
				sb.Append('.').Append(Advance().Text);
			}
			project = sb.ToString();
			if (!AtSeparatorOrEnd) { ErrorExpected("конец строки после имени проекта"); SyncMember(); }
		}

		while (true)
		{
			SkipSeparators();
			if (AtEof) break;
			int before = _p;

			if (At(TokenKind.KwUse))
			{
				var u = ParseUse();
				if (u is not null) uses.Add(u);
			}
			else if (At(TokenKind.RBrace))
			{
				ErrorExpected("объявление");
				Advance();
			}
			else
			{
				var d = ParseDecl(topLevel: true);
				if (d is not null) decls.Add(d);
				else SyncToDecl();
			}

			if (_p == before) { Advance(); }
			// После объявления должен идти разделитель (иначе — ошибка и синхронизация).
			if (!AtEof && !AtSeparatorOrEnd && _t[_p - 1].Kind != TokenKind.RBrace)
			{
				ErrorExpected("конец объявления (перевод строки)");
				SyncToDecl();
			}
		}

		return new CompilationUnit(_file, project, uses, decls, new TextSpan(start, _file.Text.Length));
	}

	/// <summary>`use a.b[X, Y]`, `use a.b.*`.</summary>
	private UseDecl? ParseUse()
	{
		int start = StartPos;
		Advance(); // use
		var path = new List<string>();
		var items = new List<string>();
		bool wildcard = false;

		var (first, _) = ExpectIdent("путь модуля");
		if (first == "<missing>") { SyncMember(); return null; }
		path.Add(first);
		while (At(TokenKind.Dot))
		{
			Advance();
			if (Eat(TokenKind.Star)) { wildcard = true; break; }
			var (n, _) = ExpectIdent("имя модуля");
			if (n == "<missing>") break;
			path.Add(n);
		}

		if (!wildcard && At(TokenKind.LBracket))
		{
			Advance();
			_ignoreNl++;
			while (!At(TokenKind.RBracket) && !AtEof)
			{
				var (n, _) = ExpectIdent("имя элемента импорта");
				if (n == "<missing>") break;
				items.Add(n);
				if (!Eat(TokenKind.Comma)) break;
			}
			Expect(TokenKind.RBracket, "']'");
			_ignoreNl--;
		}
		return new UseDecl(path, items, wildcard, SpanFrom(start));
	}
}
