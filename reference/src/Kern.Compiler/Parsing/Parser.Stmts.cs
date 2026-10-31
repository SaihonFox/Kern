using Kern.Compiler.Lexing;
using Kern.Compiler.Syntax;
using Kern.Core;

namespace Kern.Compiler.Parsing;

// Блоки, объявления переменных, if / match / loop.
public sealed partial class Parser
{
	/// <summary>Результат спекулятивного разбора `T x`, `T x(args)`, `T! x`.</summary>
	private sealed record DeclHead(TypeRef Type, string Name, TextSpan NameSpan, bool Single, List<Argument>? CtorArgs);

	/// <summary>Ветка/тело: блок `{}` либо одно выражение (в т.ч. объявление переменной).</summary>
	private Expr ParseBranch()
	{
		if (At(TokenKind.LBrace)) return ParseBlock();
		int start = StartPos;
		var stmt = ParseStatement();
		if (_pendingStmts.Count == 0) return stmt;
		var all = new List<Expr> { stmt };
		all.AddRange(_pendingStmts);
		_pendingStmts.Clear();
		return new BlockExpr(all, SpanFrom(start));
	}

	/// <summary>Хвост `i32 a = 1, b = 2, c`: остальные объявления выдаёт ParseBlock следом за первым.</summary>
	private readonly List<Expr> _pendingStmts = [];

	/// <summary>Локальные функции `fun name(...)` (разобраны как переменная-лямбда); сравнение по ссылке.</summary>
	private readonly HashSet<Expr> _localFuns = new(ReferenceEqualityComparer.Instance);

	/// <summary>`{ stmt (NewLine | ;) stmt ... }` — значение последнего выражения.</summary>
	private BlockExpr ParseBlock()
	{
		int start = StartPos;
		Expect(TokenKind.LBrace, "'{'");
		int saved = EnterBlock();
		int ns = _noRangeStep;
		_noRangeStep = 0;
		var list = new List<Expr>();
		var starts = new List<int>();
		int firstTok = _p;
		while (true)
		{
			SkipSeparators();
			if (At(TokenKind.RBrace) || AtEof) break;
			int before = _p;
			starts.Add(_p);
			list.Add(ParseStatement());
			if (_pendingStmts.Count > 0)
			{
				foreach (var extra in _pendingStmts) { list.Add(extra); starts.Add(_p); }
				_pendingStmts.Clear();
			}
			if (_p == before) Advance(); // гарантия прогресса
			if (!AtSeparatorOrEnd)
			{
				ErrorExpected("конец выражения (перевод строки или ';')");
				SyncMember();
			}
		}
		Expect(TokenKind.RBrace, "'}'");
		_noRangeStep = ns;
		ExitBlock(saved);
		HoistLocalFunctions(list, starts, firstTok);
		return new BlockExpr(list, SpanFrom(start));
	}

	/// <summary>
	/// Локальная функция, имя которой встречается в блоке раньше её объявления, поднимается в начало блока
	/// (вызов до объявления, взаимная рекурсия). Остальные остаются на месте и видят все ранее объявленные переменные.
	/// </summary>
	private void HoistLocalFunctions(List<Expr> list, List<int> starts, int firstTok)
	{
		if (_localFuns.Count == 0) return;
		var hoisted = new List<Expr>();
		for (int i = 0; i < list.Count; i++)
		{
			if (!_localFuns.Contains(list[i]) || list[i] is not VarDeclExpr vd) continue;
			bool usedBefore = false;
			for (int t = firstTok; t < starts[i] && t < _t.Count; t++)
				if (_t[t].Kind == TokenKind.Ident && _t[t].Text == vd.Name) { usedBefore = true; break; }
			if (usedBefore) hoisted.Add(list[i]);
		}
		if (hoisted.Count == 0) return;
		foreach (var h in hoisted) list.Remove(h);
		list.InsertRange(0, hoisted);
	}

	/// <summary>Оператор: объявление переменной, деструктуризация или выражение.</summary>
	private Expr ParseStatement()
	{
		int start = StartPos;
		if (Kind == TokenKind.KwUse)
		{
			// `use T x = e` / `use var x = e` — объявление с авто-вызовом dispose() при выходе из блока
			Advance();
			if (Kind is not (TokenKind.KwVar or TokenKind.Ident or TokenKind.LParen))
			{
				ErrorExpected("объявление переменной после `use`");
				return new ErrorExpr("", new TextSpan(StartPos, 0));
			}
			var decl = ParseStatement();
			if (decl is VarDeclExpr vdu) return vdu with { IsUse = true, Span = SpanFrom(start) };
			Error(decl.Span, "после `use` ожидалось объявление переменной (`use T x = …`)");
			return decl;
		}
		switch (Kind)
		{
			case TokenKind.KwVar:
			{
				Advance();
				bool single = Eat(TokenKind.Bang);
				var (name, nameSpan) = ExpectIdent("имя переменной");
				Expr? init = null;
				if (At(TokenKind.Eq))
				{
					Advance();
					SkipNewLines();
					init = ParseExpr();
				}
				else ErrorExpected("'=' (var требует инициализатор)");
				return new VarDeclExpr(null, name, nameSpan, init, null, single, SpanFrom(start));
			}
			case TokenKind.LParen:
			{
				var d = TryParseDestructureStatement();
				if (d is not null) return d;
				break;
			}
			case TokenKind.Ident when Peek(1).Kind is TokenKind.LParen:
			{
				var d = TryParseDestructureStatement();
				if (d is not null) return d;
				break;
			}
		}

		if (Kind == TokenKind.KwFun && Peek(1).Kind == TokenKind.Ident) return ParseLocalFunction(false);
		if (Kind == TokenKind.KwAsync && Peek(1).Kind == TokenKind.KwFun && Peek(2).Kind == TokenKind.Ident)
		{
			Advance();
			return ParseLocalFunction(true);
		}

		if (Kind is TokenKind.KwFun or TokenKind.LParen
			|| (Kind == TokenKind.Ident
				&& Peek(1).Kind is TokenKind.Ident or TokenKind.Lt or TokenKind.Dot or TokenKind.Question
					or TokenKind.Bang or TokenKind.LParen))
		{
			var head = Speculate(TryParseDeclHead);
			if (head is not null)
			{
				Expr? init = null;
				if (head.CtorArgs is null && At(TokenKind.Eq))
				{
					Advance();
					SkipNewLines();
					init = ParseExpr();
				}
				var first = new VarDeclExpr(head.Type, head.Name, head.NameSpan, init, head.CtorArgs, head.Single, SpanFrom(start));
				// `i32 a = 1, b = 2, c` — следующие объявления того же типа
				while (At(TokenKind.Comma))
				{
					int ds = StartPos;
					Advance();
					var (n, ns) = ExpectIdent("имя переменной");
					Expr? i2 = null;
					List<Argument>? ctor2 = null;
					if (At(TokenKind.Eq)) { Advance(); SkipNewLines(); i2 = ParseExpr(); }
					else if (At(TokenKind.LParen)) ctor2 = ParseArgList();
					_pendingStmts.Add(new VarDeclExpr(head.Type, n, ns, i2, ctor2, head.Single, SpanFrom(ds)));
				}
				return first;
			}
		}

		// `a, b = b, a` — одновременное присваивание (swap): правые части вычисляются первыми
		if (Kind is TokenKind.Ident or TokenKind.Dollar && Probe(() => { ParseTernary(); if (!At(TokenKind.Comma)) return false; while (Eat(TokenKind.Comma)) ParseTernary(); return At(TokenKind.Eq); }))
			return ParseMultiAssign(start);

		return ParseExpr();
	}

	private int _tmpCounter;

	/// <summary>`t1, t2 = e1, e2` → `{ var $m0 = e1; var $m1 = e2; t1 = $m0; t2 = $m1 }`.</summary>
	private Expr ParseMultiAssign(int start)
	{
		var targets = new List<Expr> { ParseTernary() };
		while (Eat(TokenKind.Comma)) targets.Add(ParseTernary());
		if (!Expect(TokenKind.Eq, "'='")) return targets[0];
		SkipNewLines();
		var values = new List<Expr> { ParseTernary() };
		while (Eat(TokenKind.Comma)) { SkipNewLines(); values.Add(ParseTernary()); }
		var span = SpanFrom(start);
		if (targets.Count != values.Count)
		{
			Error(span, $"слева {targets.Count} значений, справа {values.Count}");
			return targets[0];
		}
		var stmts = new List<Expr>();
		var names = new List<string>();
		foreach (var v in values)
		{
			string tmp = $"$m{_tmpCounter++}";
			names.Add(tmp);
			stmts.Add(new VarDeclExpr(null, tmp, v.Span, v, null, false, v.Span));
		}
		for (int i = 0; i < targets.Count; i++)
			stmts.Add(new AssignExpr(AssignOp.Assign, targets[i], new IdentExpr(names[i], values[i].Span), span));
		return new BlockExpr(stmts, span);
	}

	/// <summary>`fun name(params) [-> R] body` внутри тела → `fun(P..) -> R name = fun(params) -> R body`.</summary>
	private Expr ParseLocalFunction(bool isAsync)
	{
		int start = StartPos;
		Advance(); // fun
		var (name, nameSpan) = ExpectIdent("имя функции");
		List<Param> ps = At(TokenKind.LParen) ? ParseParams() : [];
		TypeRef ret = new NamedType("void", [], nameSpan);
		if (At(TokenKind.Arrow))
		{
			Advance();
			ret = ParseType();
		}
		if (isAsync) ret = new NamedType("task", [ret], ret.Span);
		var body = ParseBody();
		var span = SpanFrom(start);
		var lambda = new LambdaExpr(isAsync, ps, ret, body, span);
		var type = new Syntax.FuncType([.. ps.Select(p => p.Type)], ret, span);
		var decl = new VarDeclExpr(type, name, nameSpan, lambda, null, false, span);
		_localFuns.Add(decl);
		return decl;
	}

	/// <summary>`T x`, `T x = ...`, `T x(args)`, `T! x`: проверяется и хвост — после имени должен идти допустимый токен.</summary>
	private DeclHead? TryParseDeclHead()
	{
		var type = ParseDeclType(out bool single);
		var tok = Cur;
		if (tok.Kind != TokenKind.Ident) return null;
		Advance();
		List<Argument>? ctor = null;
		switch (Kind)
		{
			case TokenKind.Eq or TokenKind.NewLine or TokenKind.Semicolon or TokenKind.RBrace or TokenKind.Eof or TokenKind.Comma:
				break;
			case TokenKind.LParen:
				ctor = ParseArgList();
				if (!AtSeparatorOrEnd) return null;
				break;
			default:
				return null;
		}
		return new DeclHead(type, tok.Text, tok.Span, single, ctor);
	}

	/// <summary>`(i32 a, _, ..rest) = e` и `Point(x, y) = e`.</summary>
	private Expr? TryParseDestructureStatement()
	{
		int start = StartPos;
		var pat = Speculate<DestructurePattern>(() =>
			ParsePattern() is DestructurePattern p && At(TokenKind.Eq) ? p : null);
		if (pat is null) return null;
		Advance(); // =
		SkipNewLines();
		var value = ParseExpr();
		return new DestructureExpr(pat, value, SpanFrom(start));
	}

	// ---------------- if ----------------

	/// <summary>`if c a [else b]`. `else` — на той же строке, что `}` (для ветки без {} допустим и перенос).</summary>
	private Expr ParseIf()
	{
		int start = StartPos;
		Advance(); // if
		var cond = ParseExpr();
		SkipNewLines();
		var then = ParseBranch();

		bool thenIsBlock = then is BlockExpr && _t[_p - 1].Kind == TokenKind.RBrace;
		if (!thenIsBlock && Cur.Kind == TokenKind.NewLine && PeekPastNewLines().Kind == TokenKind.KwElse)
			SkipNewLines();

		Expr? els = null;
		if (At(TokenKind.KwElse))
		{
			Advance();
			SkipNewLines();
			els = ParseBranch();
		}
		return new IfExpr(cond, then, els, SpanFrom(start));
	}

	// ---------------- match ----------------

	/// <summary>`match [target] { pattern [if guard] => expr; ... }`.</summary>
	private Expr ParseMatch()
	{
		int start = StartPos;
		Advance(); // match
		Expr? target = null;
		if (!At(TokenKind.LBrace) && !(At(TokenKind.NewLine) && PeekPastNewLines().Kind == TokenKind.LBrace))
			target = ParseExpr();
		SkipNewLines();

		var arms = new List<MatchArm>();
		if (!Expect(TokenKind.LBrace, "'{'"))
			return new MatchExpr(target, arms, SpanFrom(start));
		int saved = EnterBlock();
		int ns = _noRangeStep;
		_noRangeStep = 0;
		while (true)
		{
			SkipSeparators();
			if (At(TokenKind.RBrace) || AtEof) break;
			int before = _p;
			arms.Add(ParseMatchArm(pureGuards: target is null));
			if (_p == before) Advance();
			if (!AtSeparatorOrEnd)
			{
				ErrorExpected("конец ветки (перевод строки или ';')");
				SyncMember();
			}
		}
		Expect(TokenKind.RBrace, "'}'");
		_noRangeStep = ns;
		ExitBlock(saved);
		return new MatchExpr(target, arms, SpanFrom(start));
	}

	private MatchArm ParseMatchArm(bool pureGuards)
	{
		int start = StartPos;
		Pattern pat;
		Expr? guard = null;
		if (pureGuards)
		{
			// `match { x > 0 => ... }`: условие хранится как ValuePattern; `_` — catch-all.
			if (At(TokenKind.Underscore) && Peek(1).Kind is TokenKind.FatArrow or TokenKind.Arrow)
			{
				var u = Advance();
				pat = new WildcardPattern(u.Span);
			}
			else
			{
				var cond = ParseExpr();
				pat = new ValuePattern(cond, cond.Span);
			}
		}
		else
		{
			pat = ParsePattern();
			// охрана: `паттерн if cond => …` или короче `Тип x: cond => …`
			if (At(TokenKind.KwIf) || At(TokenKind.Colon))
			{
				Advance();
				guard = ParseExpr();
			}
		}
		if (At(TokenKind.Arrow))
		{
			// старый синтаксис: подсказываем и продолжаем разбор, чтобы не плодить каскад ошибок
			ErrorExpected("'=>' (в ветках match стрелка `=>`, а `->` — для лямбд и типов результата)");
			Advance();
		}
		else Expect(TokenKind.FatArrow, "'=>'");
		SkipNewLines();
		var body = ParseBranch();
		return new MatchArm(pat, guard, body, SpanFrom(start));
	}

	// ---------------- loop ----------------

	/// <summary>`loop T x in iterable body` / `loop var x in …` / `loop x in …` (x уже объявлена) / `loop 0..5 body`.</summary>
	private Expr ParseLoop(bool isAsync = false)
	{
		int start = StartPos;
		Advance(); // loop

		if (!isAsync)
		{
			// `loop { … }` — бесконечный цикл
			if (At(TokenKind.LBrace))
			{
				var loopBody = ParseBranch();
				// do-while: `loop { … }: cond` (двоеточие может стоять на следующей строке)
				if (At(TokenKind.NewLine) && Peek(1).Kind == TokenKind.Colon) SkipNewLines();
				if (At(TokenKind.Colon))
				{
					Advance();
					var post = ParseExpr();
					return new WhileExpr(post, loopBody, SpanFrom(start), PostCondition: true);
				}
				return new WhileExpr(null, loopBody, SpanFrom(start));
			}

			// `loop паттерн in …` — обход; иначе `loop условие тело` — while.
			// Условие с оператором `in` пишите в скобках: `loop (x in set) …`.
			var variable = Speculate<Pattern>(() =>
			{
				var p = ParsePattern();
				return At(TokenKind.KwIn) ? p : null;
			});
			if (variable is null)
			{
				var cond = ParseExpr();
				SkipNewLines();
				// `loop 0..5 тело` — повтор без переменной (диапазон вместо условия)
				if (cond is RangeExpr)
					return new LoopExpr(new WildcardPattern(new TextSpan(cond.Span.Start, 0)), cond, ParseBranch(), SpanFrom(start), false);
				return new WhileExpr(cond, ParseBranch(), SpanFrom(start));
			}
			Advance(); // in
			var items = ParseExpr();
			SkipNewLines();
			return new LoopExpr(MarkLoopTargets(variable), items, ParseBranch(), SpanFrom(start), false);
		}

		var pat = MarkLoopTargets(ParsePattern());
		Expect(TokenKind.KwIn, "'in'");
		var iterable = ParseExpr();
		SkipNewLines();
		var body = ParseBranch();
		return new LoopExpr(pat, iterable, body, SpanFrom(start), isAsync);
	}
}
