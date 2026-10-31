using Kern.Compiler.Lexing;
using Kern.Compiler.Syntax;
using Kern.Core;

namespace Kern.Compiler.Parsing;

// Выражения: присваивание → тернарный → Pratt по бинарным операторам → унарные → постфикс → первичные.
public sealed partial class Parser
{
	// Приоритеты бинарных операторов (больше — теснее):
	// 1 ?:   2 ||   3 &&   4 in !in is !is   5 == != === !==   6 < > <= >=   7 .. ..=   8 + -   9 * / %   10 ^ (правоассоц.)
	private const int PrecRange = 7;
	private const int PrecAdditive = 8;
	private const int PrecPow = 10;

	private static bool TryBinaryInfo(TokenKind k, out int prec, out bool rightAssoc)
	{
		rightAssoc = false;
		switch (k)
		{
			case TokenKind.QuestionColon: prec = 1; rightAssoc = true; return true;
			case TokenKind.PipePipe: prec = 2; return true;
			case TokenKind.AmpAmp: prec = 3; return true;
			case TokenKind.KwIn or TokenKind.NotIn or TokenKind.KwIs or TokenKind.NotIs: prec = 4; return true;
			case TokenKind.EqEq or TokenKind.BangEq or TokenKind.EqEqEq or TokenKind.BangEqEq: prec = 5; return true;
			case TokenKind.Lt or TokenKind.Gt or TokenKind.LtEq or TokenKind.GtEq: prec = 6; return true;
			case TokenKind.DotDot or TokenKind.DotDotEq: prec = PrecRange; return true;
			case TokenKind.Plus or TokenKind.Minus: prec = PrecAdditive; return true;
			case TokenKind.Star or TokenKind.Slash or TokenKind.Percent: prec = 9; return true;
			case TokenKind.StarStar: prec = PrecPow; rightAssoc = true; return true;
			default: prec = 0; return false;
		}
	}

	private static BinaryOp ToBinaryOp(TokenKind k) => k switch
	{
		TokenKind.QuestionColon => BinaryOp.Elvis,
		TokenKind.PipePipe => BinaryOp.Or,
		TokenKind.AmpAmp => BinaryOp.And,
		TokenKind.KwIn => BinaryOp.In,
		TokenKind.NotIn => BinaryOp.NotIn,
		TokenKind.EqEq => BinaryOp.Eq,
		TokenKind.BangEq => BinaryOp.Ne,
		TokenKind.EqEqEq => BinaryOp.RefEq,
		TokenKind.BangEqEq => BinaryOp.RefNe,
		TokenKind.Lt => BinaryOp.Lt,
		TokenKind.Gt => BinaryOp.Gt,
		TokenKind.LtEq => BinaryOp.Le,
		TokenKind.GtEq => BinaryOp.Ge,
		TokenKind.Plus => BinaryOp.Add,
		TokenKind.Minus => BinaryOp.Sub,
		TokenKind.Star => BinaryOp.Mul,
		TokenKind.Slash => BinaryOp.Div,
		TokenKind.Percent => BinaryOp.Rem,
		_ => BinaryOp.Pow,
	};

	private static bool TryAssignOp(TokenKind k, out AssignOp op)
	{
		op = k switch
		{
			TokenKind.Eq => AssignOp.Assign,
			TokenKind.PlusEq => AssignOp.Add,
			TokenKind.MinusEq => AssignOp.Sub,
			TokenKind.StarEq => AssignOp.Mul,
			TokenKind.SlashEq => AssignOp.Div,
			TokenKind.PercentEq => AssignOp.Rem,
			TokenKind.StarStarEq => AssignOp.Pow,
			_ => (AssignOp)(-1),
		};
		return (int)op >= 0;
	}

	/// <summary>Полное выражение, включая присваивание (правоассоциативное).</summary>
	private Expr ParseExpr()
	{
		int start = StartPos;
		var left = ParseTernary();
		if (TryAssignOp(Kind, out var op))
		{
			Advance();
			SkipNewLines();
			var right = ParseExpr();
			return new AssignExpr(op, left, right, SpanFrom(start));
		}
		return left;
	}

	/// <summary>`c ? a : b` (правоассоциативный).</summary>
	private Expr ParseTernary()
	{
		int start = StartPos;
		var cond = ParseBinary(1);
		if (!At(TokenKind.Question)) return cond;
		Advance();
		SkipNewLines();
		_noRangeStep++; // `:` здесь разделитель тернарного, а не шаг диапазона
		var then = ParseExpr();
		_noRangeStep--;
		Expect(TokenKind.Colon, "':'");
		SkipNewLines();
		var els = ParseTernary();
		return new TernaryExpr(cond, then, els, SpanFrom(start));
	}

	private Expr ParseBinary(int minPrec)
	{
		int start = StartPos;
		var left = ParseUnary();
		while (true)
		{
			var k = Kind;
			if (!TryBinaryInfo(k, out int prec, out bool rightAssoc) || prec < minPrec) break;

			if (k is TokenKind.KwIs or TokenKind.NotIs)
			{
				Advance();
				bool savedInExpr = _typeInExpr;
				_typeInExpr = true;
				var ty = ParseType();
				_typeInExpr = savedInExpr;
				left = new TypeTestExpr(left, ty, k == TokenKind.NotIs, SpanFrom(start));
				continue;
			}

			if (k is TokenKind.DotDot or TokenKind.DotDotEq)
			{
				Advance();
				left = ParseRangeRest(start, left, k == TokenKind.DotDotEq);
				continue;
			}

			Advance();
			SkipNewLines(); // оператор в конце строки — продолжение на следующей
			var right = ParseBinary(rightAssoc ? prec : prec + 1);
			left = new BinaryExpr(ToBinaryOp(k), left, right, SpanFrom(start));
		}
		return left;
	}

	/// <summary>Хвост диапазона после `..`/`..=`: необязательный конец и `:step`.</summary>
	private Expr ParseRangeRest(int start, Expr? from, bool inclusive)
	{
		Expr? end = StartsExpr(Kind) ? ParseBinary(PrecAdditive) : null;
		Expr? step = null;
		if (At(TokenKind.Colon) && _noRangeStep == 0)
		{
			Advance();
			step = ParseBinary(PrecAdditive);
		}
		return new RangeExpr(from, end, inclusive, step, SpanFrom(start));
	}

	private Expr ParseUnary()
	{
		int start = StartPos;
		switch (Kind)
		{
			case TokenKind.Minus:
				Advance();
				return new UnaryExpr(UnaryOp.Negate, ParseBinary(PrecPow), SpanFrom(start));
			case TokenKind.Plus:
				Advance();
				return new UnaryExpr(UnaryOp.Plus, ParseBinary(PrecPow), SpanFrom(start));
			case TokenKind.Bang:
				Advance();
				return new UnaryExpr(UnaryOp.Not, ParseBinary(PrecPow), SpanFrom(start));
			case TokenKind.KwAwait:
				Advance();
				return new AwaitExpr(ParseUnary(), SpanFrom(start));
			case TokenKind.DotDot or TokenKind.DotDotEq:
			{
				bool incl = Advance().Kind == TokenKind.DotDotEq;
				return ParseRangeRest(start, null, incl);
			}
		}
		return ParsePostfix(start, ParsePrimary());
	}

	// ---------------- Постфикс ----------------

	private Expr ParsePostfix(int start, Expr e)
	{
		while (true)
		{
			var k = Cur.Kind;
			if (k == TokenKind.NewLine)
			{
				// Цепочка `.foo()` / `?.foo()` с начала следующей строки продолжает выражение.
				if (PeekPastNewLines().Kind is TokenKind.Dot or TokenKind.QuestionDot) { SkipNewLines(); continue; }
				return e;
			}

			switch (k)
			{
				case TokenKind.Dot or TokenKind.QuestionDot:
				{
					Advance();
					var tok = Cur;
					if (tok.Kind == TokenKind.Ident || TokenFacts.KeywordText(tok.Kind) is not null)
					{
						Advance();
						e = new MemberExpr(e, tok.Text, k == TokenKind.QuestionDot, SpanFrom(start));
					}
					else
					{
						ErrorExpected("имя члена");
						return e;
					}
					var generic = TryGenericCall(ref e, start);
					if (generic) continue;
					continue;
				}
				case TokenKind.Lt when e is IdentExpr or MemberExpr:
					if (TryGenericCall(ref e, start)) continue;
					return e;
				case TokenKind.LParen:
					e = ParseCallRest(e, [], start);
					continue;
				case TokenKind.LBracket:
				{
					Advance();
					_ignoreNl++;
					int ns = _noRangeStep; _noRangeStep = 0;
					var idx = new List<Expr>();
					while (!At(TokenKind.RBracket) && !AtEof)
					{
						idx.Add(ParseExpr());
						if (!Eat(TokenKind.Comma)) break;
					}
					Expect(TokenKind.RBracket, "']'");
					_noRangeStep = ns;
					_ignoreNl--;
					e = new IndexExpr(e, idx, SpanFrom(start));
					continue;
				}
				case TokenKind.Bang:
					Advance();
					e = new NonNullExpr(e, SpanFrom(start));
					continue;
				default:
					return e;
			}
		}
	}

	/// <summary>
	/// Спекулятивно: `&lt;T, ...&gt;` и сразу `(` либо `[`. При успехе разбирает вызов/литерал коллекции и обновляет e.
	/// </summary>
	private bool TryGenericCall(ref Expr e, int start)
	{
		if (!At(TokenKind.Lt) || e is not (IdentExpr or MemberExpr)) return false;
		var targs = Speculate(() =>
		{
			var a = ParseTypeArgs();
			return At(TokenKind.LParen) || At(TokenKind.LBracket) ? a : null;
		});
		if (targs is null) return false;

		if (At(TokenKind.LParen))
		{
			e = ParseCallRest(e, targs, start);
			return true;
		}

		// `array<T>[..]`, `list<T>[..]`, `dict<K, V>[..]`
		string? name = (e as IdentExpr)?.Name;
		switch (name)
		{
			case "array" when targs.Count == 1:
				e = ParseBracketLiteral(targs[0], null, isArray: true, start);
				return true;
			case "list" when targs.Count == 1:
				e = ParseBracketLiteral(targs[0], null, isArray: false, start);
				return true;
			case "dict" when targs.Count == 2:
				e = ParseBracketLiteral(targs[0], targs[1], isArray: false, start);
				return true;
		}
		Error(e.Span, "литерал коллекции с типами допустим только для array, list и dict");
		e = ParseBracketLiteral(null, null, isArray: false, start);
		return true;
	}

	private Expr ParseCallRest(Expr callee, IReadOnlyList<TypeRef> targs, int start)
	{
		var args = ParseArgList();
		LambdaExpr? trailing = null;
		if (At(TokenKind.KwFun) || (At(TokenKind.KwAsync) && Peek(1).Kind == TokenKind.KwFun))
			trailing = ParseLambda();
		return new CallExpr(callee, targs, args, trailing, SpanFrom(start));
	}

	/// <summary>`(a, name: b)`; курсор на `(`. Внутри переводы строк свободны.</summary>
	private List<Argument> ParseArgList()
	{
		var list = new List<Argument>();
		Expect(TokenKind.LParen, "'('");
		_ignoreNl++;
		int ns = _noRangeStep; _noRangeStep = 0;
		while (!At(TokenKind.RParen) && !AtEof)
		{
			int s = StartPos;
			string? name = null;
			if (Kind == TokenKind.Ident && Peek(1).Kind == TokenKind.Colon)
			{
				name = Advance().Text;
				Advance();
			}
			var v = ParseExpr();
			list.Add(new Argument(name, v, SpanFrom(s)));
			if (!Eat(TokenKind.Comma)) break;
		}
		Expect(TokenKind.RParen, "')'");
		_noRangeStep = ns;
		_ignoreNl--;
		return list;
	}

	// ---------------- Первичные выражения ----------------

	private Expr ParsePrimary()
	{
		var tok = Cur;
		int start = tok.Span.Start;
		switch (tok.Kind)
		{
			case TokenKind.Number:
				Advance();
				return new NumberLitExpr(tok.Number, tok.Span);
			case TokenKind.String:
				Advance();
				return IsRegexPrefix(tok) ? RegexCtor(new StringLitExpr(tok.StringValue, tok.Span), tok.Span)
					: new StringLitExpr(tok.StringValue, tok.Span);
			case TokenKind.Char:
				Advance();
				return new CharLitExpr(tok.CharValue, tok.Span);
			case TokenKind.FString:
				Advance();
				return IsRegexPrefix(tok) ? RegexCtor(ParseInterpolated(tok), tok.Span) : ParseInterpolated(tok);
			case TokenKind.KwTrue:
				Advance();
				return new BoolLitExpr(true, tok.Span);
			case TokenKind.KwFalse:
				Advance();
				return new BoolLitExpr(false, tok.Span);
			case TokenKind.KwNil:
				Advance();
				return new NilLitExpr(tok.Span);
			case TokenKind.KwNone:
				Advance();
				return new NoneLitExpr(tok.Span);
			case TokenKind.Underscore:
				Advance();
				return new IdentExpr("_", tok.Span);

			case TokenKind.Ident:
				if (tok.Text == "typeof" && Peek(1).Kind is TokenKind.Lt or TokenKind.LParen)
					return ParseTypeof();
				Advance();
				return new IdentExpr(tok.Text, tok.Span);

			case TokenKind.Dollar:
			{
				Advance();
				var next = Cur;
				if (next.Kind == TokenKind.Ident && next.Span.Start == tok.Span.End)
				{
					Advance();
					return new SelfMemberExpr(next.Text, SpanFrom(start));
				}
				return new SelfExpr(tok.Span);
			}
			case TokenKind.DollarBracket:
			{
				Advance();
				var (parent, _) = ExpectIdent("имя родителя");
				Expect(TokenKind.RBracket, "']'");
				Expect(TokenKind.Dot, "'.'");
				var (member, _) = ExpectIdent("имя члена");
				return new ParentMemberExpr(parent, member, SpanFrom(start));
			}

			case TokenKind.LParen: return ParseParenOrTuple();
			case TokenKind.LBracket: return ParseBracketLiteral(null, null, isArray: false, start);
			case TokenKind.Lt: return ParseTypedLiteral();
			case TokenKind.LBrace: return ParseBlock();

			case TokenKind.KwIf: return ParseIf();
			case TokenKind.KwMatch: return ParseMatch();
			case TokenKind.KwLoop: return ParseLoop();
			case TokenKind.KwStop:
				Advance();
				return new StopExpr(tok.Span);
			case TokenKind.KwNext:
				Advance();
				return new NextExpr(tok.Span);
			case TokenKind.KwRet:
			{
				Advance();
				// `ret` без значения: дальше конец строки/`;`/`}`/`)`/`,`/`else`/`->` и т.п. (на той же строке нет начала выражения).
				Expr? value = StartsExpr(Kind) || At(TokenKind.Lt) ? ParseExpr() : null;
				return new ReturnExpr(value, SpanFrom(start));
			}
			case TokenKind.KwAsync when Peek(1).Kind == TokenKind.KwLoop:
				Advance(); // async
				return ParseLoop(isAsync: true);
			case TokenKind.KwFun or TokenKind.KwAsync:
				return ParseLambda();

			case TokenKind.Error:
				Advance(); // ошибка уже сообщена лексером
				return new ErrorExpr("", tok.Span);

			default:
			{
				string msg = $"ожидалось выражение, найдено {Describe(tok)}";
				Error(tok.Span, msg);
				return new ErrorExpr(msg, new TextSpan(tok.Span.Start, 0));
			}
		}
	}

	/// <summary>Литерал с префиксом `x` / `fx` — значение типа regex.</summary>
	private static bool IsRegexPrefix(Token t) => t.Text.StartsWith("x\"", StringComparison.Ordinal) || t.Text.StartsWith("fx\"", StringComparison.Ordinal);

	/// <summary>`x"…"` ≡ `kern.regex.regex("…")`: полный путь, чтобы литерал работал без `use kern.regex[regex]`.</summary>
	private static Expr RegexCtor(Expr pattern, TextSpan span) =>
		new CallExpr(
			new MemberExpr(new MemberExpr(new IdentExpr("kern", span), "regex", false, span), "regex", false, span),
			[], [new Argument(null, pattern, span)], null, span);

	/// <summary>`typeof&lt;T&gt;()` и `typeof(expr)`.</summary>
	private Expr ParseTypeof()
	{
		int start = StartPos;
		Advance(); // typeof
		if (At(TokenKind.Lt))
		{
			Advance();
			var t = ParseType();
			Expect(TokenKind.Gt, "'>'");
			Expect(TokenKind.LParen, "'('");
			Expect(TokenKind.RParen, "')'");
			return new TypeofTypeExpr(t, SpanFrom(start));
		}
		Expect(TokenKind.LParen, "'('");
		_ignoreNl++;
		var v = ParseExpr();
		Expect(TokenKind.RParen, "')'");
		_ignoreNl--;
		return new TypeofValueExpr(v, SpanFrom(start));
	}

	/// <summary>Интерполированная строка: выражения внутри разбираются под-парсером по их токенам.</summary>
	private Expr ParseInterpolated(Token tok)
	{
		var parts = new List<InterpPart>();
		foreach (var part in tok.Parts)
		{
			switch (part)
			{
				case FStringText t:
					parts.Add(new InterpText(t.Text, t.Span));
					break;
				case FStringExpr fe:
				{
					var sub = new Parser(_file, _diags, new List<Token>(fe.Tokens)) { _spec = _spec };
					var ex = sub.ParseExpr();
					if (!sub.AtEof) sub.ErrorExpected("конец выражения в интерполяции");
					if (sub._specFailed) _specFailed = true;
					parts.Add(new InterpExpr(ex, fe.Debug, fe.Format, fe.SourceText, fe.Span));
					break;
				}
			}
		}
		return new InterpolatedStringExpr(parts, tok.Span);
	}

	/// <summary>`(e)` — скобки; `(a, b)` / `(x: 1, y: 2)` / `()` — кортеж.</summary>
	private Expr ParseParenOrTuple()
	{
		int start = StartPos;
		Advance(); // (
		_ignoreNl++;
		int ns = _noRangeStep; _noRangeStep = 0;
		var els = new List<Argument>();
		bool trailingComma = false;
		while (!At(TokenKind.RParen) && !AtEof)
		{
			int s = StartPos;
			string? name = null;
			if (Kind == TokenKind.Ident && Peek(1).Kind == TokenKind.Colon)
			{
				name = Advance().Text;
				Advance();
			}
			var v = ParseExpr();
			els.Add(new Argument(name, v, SpanFrom(s)));
			trailingComma = false;
			if (!Eat(TokenKind.Comma)) break;
			trailingComma = true;
		}
		Expect(TokenKind.RParen, "')'");
		_noRangeStep = ns;
		_ignoreNl--;
		if (els.Count == 1 && els[0].Name is null && !trailingComma) return els[0].Value;
		return new TupleExpr(els, SpanFrom(start));
	}

	/// <summary>`&lt;i32&gt;[..]` и `&lt;str: i32&gt;[..]`.</summary>
	private Expr ParseTypedLiteral()
	{
		int start = StartPos;
		Advance(); // <
		var k = ParseType();
		TypeRef? v = null;
		if (Eat(TokenKind.Colon)) v = ParseType();
		Expect(TokenKind.Gt, "'>'");
		if (!At(TokenKind.LBracket))
		{
			ErrorExpected("'[' после типов литерала коллекции");
			return new ErrorExpr("литерал коллекции", SpanFrom(start));
		}
		return ParseBracketLiteral(k, v, isArray: false, start);
	}

	/// <summary>
	/// `[a, b]` → список; `[k: v]` → словарь; `[:]` — пустой словарь. typeA/typeB — явные типы:
	/// один тип — элемент (list/array), два — ключ и значение.
	/// </summary>
	private Expr ParseBracketLiteral(TypeRef? typeA, TypeRef? typeB, bool isArray, int start)
	{
		Expect(TokenKind.LBracket, "'['");
		_ignoreNl++;
		int ns = _noRangeStep; _noRangeStep = 0;
		var elems = new List<Expr>();
		var entries = new List<DictEntry>();
		bool dict = typeB is not null;

		if (At(TokenKind.Colon) && Peek(1).Kind == TokenKind.RBracket)
		{
			Advance();
			dict = true;
		}
		while (!At(TokenKind.RBracket) && !AtEof)
		{
			int s = StartPos;
			var key = ParseExpr();
			if (At(TokenKind.Colon))
			{
				Advance();
				var val = ParseExpr();
				dict = true;
				entries.Add(new DictEntry(key, val, SpanFrom(s)));
			}
			else elems.Add(key);
			if (!Eat(TokenKind.Comma)) break;
		}
		Expect(TokenKind.RBracket, "']'");
		_noRangeStep = ns;
		_ignoreNl--;

		var span = SpanFrom(start);
		if (dict)
		{
			if (elems.Count > 0) Error(span, "в литерале словаря все элементы должны быть вида ключ: значение");
			return new DictExpr(typeA, typeB, entries, span);
		}
		if (entries.Count > 0) Error(span, "смешение элементов списка и пар ключ: значение");
		return isArray ? new ArrayExpr(typeA, elems, span) : new ListExpr(typeA, elems, span);
	}

	/// <summary>`[async] fun [(params)] [-> R] body`.</summary>
	private LambdaExpr ParseLambda()
	{
		int start = StartPos;
		bool isAsync = Eat(TokenKind.KwAsync);
		Expect(TokenKind.KwFun, "'fun'");
		List<Param> ps = At(TokenKind.LParen) ? ParseParams() : [];
		TypeRef? ret = null;
		if (At(TokenKind.Arrow))
		{
			Advance();
			ret = ParseType();
		}
		var body = ParseBody();
		return new LambdaExpr(isAsync, ps, ret, body, SpanFrom(start));
	}
}
