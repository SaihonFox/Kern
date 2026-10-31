using Kern.Compiler.Lexing;
using Kern.Compiler.Syntax;
using Kern.Core;

namespace Kern.Compiler.Parsing;

// Паттерны: match-ветки, переменная loop, деструктуризация.
public sealed partial class Parser
{
	/// <summary>Пробный разбор: всегда откатывает курсор; true — если f вернул true без ошибок.</summary>
	private bool Probe(Func<bool> f)
	{
		int p = _p, prev = _prevEnd, nl = _ignoreNl, ns = _noRangeStep;
		bool inExpr = _typeInExpr, oldFail = _specFailed;
		_spec++;
		_specFailed = false;
		bool r;
		try { r = f(); }
		finally { _spec--; }
		bool failed = _specFailed;
		_p = p; _prevEnd = prev; _ignoreNl = nl; _noRangeStep = ns; _typeInExpr = inExpr; _specFailed = oldFail;
		return r && !failed;
	}

	private Pattern ParsePattern()
	{
		int start = StartPos;
		switch (Kind)
		{
			case TokenKind.Underscore:
			{
				var u = Advance();
				return new WildcardPattern(u.Span);
			}
			case TokenKind.KwVar:
			{
				// `var x` — объявление с выводом типа (`loop var i in 0..5`, `(var k, var v)`)
				Advance();
				if (At(TokenKind.Underscore))
				{
					var u2 = Advance();
					return new WildcardPattern(u2.Span);
				}
				if (!At(TokenKind.Ident))
				{
					ErrorExpected("имя переменной после `var`");
					return new WildcardPattern(new TextSpan(Cur.Span.Start, 0));
				}
				var vid = Advance();
				return new BindingPattern(vid.Text, SpanFrom(start)) { FromVar = true };
			}
			case TokenKind.DotDot:
				return ParseRestPattern(start, null);
			case TokenKind.LParen:
				return new DestructurePattern(null, ParsePatternList(), SpanFrom(start)) is var d
					? d with { Span = SpanFrom(start) } : d;
			case TokenKind.Ident:
				return ParseIdentPattern(start);
			default:
				if (StartsExpr(Kind))
					return new ValuePattern(ParseBinary(PrecRange), default) is var v
						? v with { Span = v.Value.Span } : v;
				ErrorExpected("образец");
				return new WildcardPattern(new TextSpan(Cur.Span.Start, 0));
		}
	}

	/// <summary>`( p, p, ... )`; курсор на `(`.</summary>
	private List<Pattern> ParsePatternList()
	{
		var list = new List<Pattern>();
		Expect(TokenKind.LParen, "'('");
		_ignoreNl++;
		while (!At(TokenKind.RParen) && !AtEof)
		{
			int before = _p;
			if (At(TokenKind.Ident) && Peek(1).Kind == TokenKind.Colon)
			{
				// именованное поле `name: pattern` (структурный паттерн unit/класса)
				int ns = StartPos;
				string fname = Advance().Text;
				Advance(); // :
				list.Add(new NamedPattern(fname, ParsePattern(), SpanFrom(ns)));
			}
			else list.Add(ParsePattern());
			if (_p == before) break;
			if (!Eat(TokenKind.Comma)) break;
		}
		Expect(TokenKind.RParen, "')'");
		_ignoreNl--;
		return list;
	}

	/// <summary>`..` / `..rest` / `T ..rest`; курсор на `..`.</summary>
	private Pattern ParseRestPattern(int start, TypeRef? type)
	{
		Advance(); // ..
		string? name = null;
		if (At(TokenKind.Ident)) name = Advance().Text;
		return new RestPattern(type, name, SpanFrom(start));
	}

	/// <summary>
	/// Голова `loop`: голые имена (без типа и без `var`) — присваивание существующей переменной, а не объявление.
	/// Рекурсивно для элементов кортежа.
	/// </summary>
	private static Pattern MarkLoopTargets(Pattern p) => p switch
	{
		BindingPattern { FromVar: false } b => b with { Declares = false },
		DestructurePattern { Type: null } d => d with { Elements = [.. d.Elements.Select(MarkLoopTargets)] },
		_ => p,
	};

	private Pattern ParseIdentPattern(int start)
	{
		// (a) `Type name`, `Type _`, `Type ..rest`
		var typed = Speculate<Pattern>(() =>
		{
			var t = ParseType();
			if (At(TokenKind.DotDot)) return ParseRestPattern(start, t);
			if (At(TokenKind.Underscore))
			{
				Advance();
				return new TypePattern(t, null, SpanFrom(start));
			}
			if (At(TokenKind.Ident))
			{
				string n = Advance().Text;
				return new TypePattern(t, n, SpanFrom(start));
			}
			return null;
		});
		if (typed is not null) return typed;

		// (b) `Type(p1, p2)` — деструктуризация; ошибки внутри скобок сообщаются как обычно
		if (Probe(() => { ParseType(); return At(TokenKind.LParen); }))
		{
			var t = ParseType();
			var els = ParsePatternList();
			return new DestructurePattern(t, els, SpanFrom(start));
		}

		// (c) одиночный идентификатор — связывание
		if (Peek(1).Kind is not (TokenKind.Dot or TokenKind.Lt or TokenKind.LParen))
		{
			var id = Advance();
			return new BindingPattern(id.Text, id.Span);
		}

		// (d) значение: `Color.red`, константное выражение
		var value = ParseBinary(PrecRange);
		return new ValuePattern(value, value.Span);
	}
}
