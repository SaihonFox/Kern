using Kern.Compiler.Lexing;
using Kern.Compiler.Syntax;
using Kern.Core;

namespace Kern.Compiler.Parsing;

// Объявления: атрибуты, модификаторы, параметры, fun / type / unit / attribute / op / ctor.
public sealed partial class Parser
{
	/// <summary>Объявление верхнего уровня или члена типа; null — ошибка (токены не гарантированно потреблены).</summary>
	private Decl? ParseDecl(bool topLevel)
	{
		int start = StartPos;
		var attrs = ParseAttributes();
		var mods = ParseModifiers();

		switch (Kind)
		{
			case TokenKind.KwFun: return ParseFun(start, attrs, mods);
			case TokenKind.KwType: return ParseTypeDecl(start, attrs, mods);
			case TokenKind.KwUnit: return ParseTypeDecl(start, attrs, mods);
			case TokenKind.KwAttribute: return ParseTypeDecl(start, attrs, mods);
			case TokenKind.KwEnum: return ParseEnum(start, attrs, mods);
			case TokenKind.KwOp: return ParseOp(start, attrs, mods);
			case TokenKind.KwCtor:
				if (topLevel) { Error(Cur.Span, "ctor допустим только внутри типа"); }
				return ParseCtor(start, attrs, mods);
			default: return ParseFieldOrProperty(start, attrs, mods);
		}
	}

	/// <summary>`#[A(args), B]` — по AttributeUse на элемент; несколько групп подряд.</summary>
	private List<AttributeUse> ParseAttributes()
	{
		var list = new List<AttributeUse>();
		while (At(TokenKind.HashBracket))
		{
			Advance();
			_ignoreNl++;
			do
			{
				if (At(TokenKind.RBracket)) break; // допускаем хвостовую запятую
				int s = StartPos;
				var (name, _) = ExpectIdent("имя атрибута");
				var sb = new System.Text.StringBuilder(name);
				while (At(TokenKind.Dot) && Peek(1).Kind == TokenKind.Ident)
				{
					Advance();
					sb.Append('.').Append(Advance().Text);
				}
				var args = At(TokenKind.LParen) ? ParseArgList() : [];
				list.Add(new AttributeUse(sb.ToString(), args, SpanFrom(s)));
				if (name == "<missing>") break;
			} while (Eat(TokenKind.Comma));
			Expect(TokenKind.RBracket, "']'");
			_ignoreNl--;
			SkipNewLines(false);
		}
		return list;
	}

	/// <summary>pub/priv/prot/int, open, `~`, `@`, `#`, async, `!` (обязательное поле) — в любом порядке.</summary>
	private Modifiers ParseModifiers()
	{
		var access = Access.Default;
		var flags = MemberFlags.None;
		while (true)
		{
			switch (Kind)
			{
				case TokenKind.KwPub: access = Access.Pub; break;
				case TokenKind.KwPriv: access = Access.Priv; break;
				case TokenKind.KwProt: access = Access.Prot; break;
				case TokenKind.KwInt: access = Access.Int; break;
				case TokenKind.KwOpen: flags |= MemberFlags.Open; break;
				case TokenKind.Tilde: flags |= MemberFlags.Override; break;
				case TokenKind.At: flags |= MemberFlags.Abstract; break;
				case TokenKind.Hash: flags |= MemberFlags.Static; break;
				case TokenKind.KwAsync: flags |= MemberFlags.Async; break;
				case TokenKind.Bang: flags |= MemberFlags.Required; break;
				default: return new Modifiers(access, flags);
			}
			Advance();
		}
	}

	// ---------------- Параметры ----------------

	/// <summary>`(T a, T b = e, T ..rest, T $auto)`; курсор на `(`.</summary>
	private List<Param> ParseParams()
	{
		var list = new List<Param>();
		Expect(TokenKind.LParen, "'('");
		_ignoreNl++;
		bool ok = true;
		while (!At(TokenKind.RParen) && !AtEof)
		{
			var p = ParseParam();
			if (p is null) { ok = false; break; }
			list.Add(p);
			if (!Eat(TokenKind.Comma)) break;
		}
		if (!ok)
		{
			// Пропуск до закрывающей скобки (не уходя за { } и конец файла).
			int depth = 0;
			while (!AtEof && !At(TokenKind.LBrace) && !At(TokenKind.RBrace))
			{
				if (At(TokenKind.LParen)) depth++;
				else if (At(TokenKind.RParen)) { if (depth == 0) break; depth--; }
				Advance();
			}
		}
		Expect(TokenKind.RParen, "')'");
		_ignoreNl--;
		return list;
	}

	private Param? ParseParam()
	{
		int start = StartPos;
		var attrs = ParseAttributes();
		var mods = ParseParamModifiers();
		var type = ParseType();
		bool variadic = false, auto = false;
		if (At(TokenKind.DotDot)) { Error(Cur.Span, "`..` в параметрах не поддерживается: передавайте list<T>"); Advance(); }
		else if (At(TokenKind.Dollar)) { Advance(); auto = true; }
		var (name, nameSpan) = ExpectIdent("имя параметра");
		if (name == "<missing>") return null;
		Expr? def = null;
		if (At(TokenKind.Eq))
		{
			Advance();
			def = ParseExpr();
		}
		return new Param(attrs, mods, type, name, nameSpan, def, variadic, auto, SpanFrom(start));
	}

	/// <summary>Модификаторы параметра первичного конструктора (`pub str name`).</summary>
	private Modifiers ParseParamModifiers()
	{
		var access = Access.Default;
		while (true)
		{
			switch (Kind)
			{
				case TokenKind.KwPub: access = Access.Pub; break;
				case TokenKind.KwPriv: access = Access.Priv; break;
				case TokenKind.KwProt: access = Access.Prot; break;
				case TokenKind.KwInt: access = Access.Int; break;
				default: return new Modifiers(access, MemberFlags.None);
			}
			Advance();
		}
	}

	// ---------------- Функции ----------------

	/// <summary>Тело: блок или одно выражение; может начинаться на следующей строке.</summary>
	private Expr ParseBody()
	{
		SkipNewLines();
		return ParseBranch();
	}

	/// <summary>`fun name`, `fun name(params) -> R body`, `fun $[A].name(...)`, `fun T.name(...)`.</summary>
	private Decl? ParseFun(int start, List<AttributeUse> attrs, Modifiers mods)
	{
		Advance(); // fun
		string? parentSel = null;
		TypeRef? receiver = null;
		string name;
		TextSpan nameSpan;

		if (At(TokenKind.DollarBracket))
		{
			Advance();
			var (p, _) = ExpectIdent("имя родителя");
			Expect(TokenKind.RBracket, "']'");
			Expect(TokenKind.Dot, "'.'");
			parentSel = p;
			(name, nameSpan) = ExpectMemberName("имя функции");
		}
		else if (IsExtensionAhead())
		{
			ParseReceiverAndName(out receiver, out name, out nameSpan);
		}
		else
		{
			(name, nameSpan) = ExpectMemberName("имя функции");
		}
		if (name == "<missing>") return null;

		var tparams = ParseTypeParams();
		bool hasParams = false;
		List<Param> ps = [];
		if (At(TokenKind.LParen))
		{
			hasParams = true;
			ps = ParseParams();
		}
		TypeRef? ret = null;
		if (At(TokenKind.Arrow))
		{
			Advance();
			ret = ParseType();
		}

		if (receiver is not null)
		{
			var body = ParseBody();
			return new ExtensionFunDecl(attrs, mods, receiver, name, nameSpan, tparams, ps, ret, body, SpanFrom(start));
		}

		// Без тела: абстрактный `@fun` и выбор родителя `fun $[A].foo(...)`.
		Expr? fbody = null;
		if (!mods.IsAbstract && parentSel is null)
			fbody = ParseBody();
		return new FunDecl(attrs, mods, name, nameSpan, tparams, parentSel, ps, hasParams, ret, fbody, SpanFrom(start));
	}

	/// <summary>После `fun`: `Ident(.Ident)+`, `Ident&lt;..&gt;.`, `Ident?.` — расширение.</summary>
	private bool IsExtensionAhead()
	{
		if (Kind != TokenKind.Ident) return false;
		if (Peek(1).Kind == TokenKind.Dot && Peek(2).Kind == TokenKind.Ident) return true;
		if (Peek(1).Kind == TokenKind.Question && Peek(2).Kind == TokenKind.Dot) return true;
		if (Peek(1).Kind == TokenKind.Lt)
		{
			int depth = 0, j = 1;
			while (true)
			{
				var k = Peek(j).Kind;
				if (k == TokenKind.Lt) depth++;
				else if (k == TokenKind.Gt) { depth--; if (depth == 0) break; }
				else if (k is TokenKind.Eof or TokenKind.NewLine or TokenKind.LParen or TokenKind.LBrace) return false;
				j++;
			}
			return Peek(j + 1).Kind == TokenKind.Dot;
		}
		return false;
	}

	/// <summary>Разбирает `Receiver.name`: ParseType жадно съедает `str.shout`, поэтому последний сегмент отделяется.</summary>
	private void ParseReceiverAndName(out TypeRef receiver, out string name, out TextSpan nameSpan)
	{
		var t = ParseType();
		if (At(TokenKind.Dot))
		{
			Advance();
			receiver = t;
			var tok = Cur;
			if (tok.Kind == TokenKind.Ident) { Advance(); name = tok.Text; nameSpan = tok.Span; }
			else { ErrorExpected("имя члена"); name = "<missing>"; nameSpan = new TextSpan(tok.Span.Start, 0); }
			return;
		}
		if (t is NamedType { TypeArgs.Count: 0 } nt)
		{
			int k = nt.Name.LastIndexOf('.');
			if (k > 0)
			{
				receiver = new NamedType(nt.Name[..k], [], TextSpan.FromBounds(nt.Span.Start, nt.Span.Start + k));
				name = nt.Name[(k + 1)..];
				nameSpan = TextSpan.FromBounds(nt.Span.Start + k + 1, nt.Span.End);
				return;
			}
		}
		ErrorExpected("'.' и имя члена расширения");
		receiver = t;
		name = "<missing>";
		nameSpan = new TextSpan(Cur.Span.Start, 0);
	}

	// ---------------- Операторы и конструкторы ----------------

	private static bool IsOperatorToken(TokenKind k) => k is
		TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash or TokenKind.Percent or TokenKind.StarStar or TokenKind.Caret
		or TokenKind.EqEq or TokenKind.BangEq or TokenKind.EqEqEq or TokenKind.BangEqEq
		or TokenKind.Lt or TokenKind.Gt or TokenKind.LtEq or TokenKind.GtEq
		or TokenKind.AmpAmp or TokenKind.PipePipe or TokenKind.Bang or TokenKind.KwIn or TokenKind.NotIn
		or TokenKind.DotDot or TokenKind.DotDotEq;

	/// <summary>`op +(T o) -> R body` и `op i32.*(str s) -> str body`.</summary>
	private Decl? ParseOp(int start, List<AttributeUse> attrs, Modifiers mods)
	{
		Advance(); // op
		TypeRef? receiver = null;
		if (Kind == TokenKind.Ident && Peek(1).Kind is TokenKind.Dot or TokenKind.Lt or TokenKind.Question)
		{
			receiver = ParseType();
			Expect(TokenKind.Dot, "'.'");
		}

		string symbol;
		if (At(TokenKind.LBracket))
		{
			Advance();
			Expect(TokenKind.RBracket, "']'");
			symbol = "[]";
		}
		else if (IsOperatorToken(Kind))
		{
			symbol = Advance().Text;
		}
		else
		{
			ErrorExpected("символ оператора");
			return null;
		}

		List<Param> ps = At(TokenKind.LParen) ? ParseParams() : [];
		TypeRef? ret = null;
		if (At(TokenKind.Arrow))
		{
			Advance();
			ret = ParseType();
		}
		var body = ParseBody();
		return receiver is null
			? new OpDecl(attrs, mods, symbol, ps, ret, body, SpanFrom(start))
			: new ExtensionOpDecl(attrs, mods, receiver, symbol, ps, ret, body, SpanFrom(start));
	}

	/// <summary>`ctor(params) [body]` — тело необязательно (`ctor(str $name)`).</summary>
	private Decl? ParseCtor(int start, List<AttributeUse> attrs, Modifiers mods)
	{
		Advance(); // ctor
		List<Param> ps = [];
		if (At(TokenKind.LParen)) ps = ParseParams();
		else ErrorExpected("'(' после ctor");

		List<Argument>? chain = null;
		if (At(TokenKind.Colon) && Peek(1).Kind == TokenKind.Dollar && Peek(2).Kind == TokenKind.LParen)
		{
			Advance(); Advance();
			chain = ParseArgList();
		}

		Expr? body = null;
		if (At(TokenKind.LBrace)) body = ParseBlock();
		else if (!AtSeparatorOrEnd) body = ParseBranch();
		else if (PeekPastNewLines().Kind == TokenKind.LBrace)
		{
			SkipNewLines();
			body = ParseBlock();
		}
		return new CtorDecl(attrs, mods, ps, body, SpanFrom(start), chain);
	}

	// ---------------- type / unit / attribute ----------------

	/// <summary>`type X&lt;T&gt;(params)[Parents] { members }`, `unit`, `unit!`, `attribute`, `type A = [T1, T2]`.</summary>
	private Decl? ParseTypeDecl(int start, List<AttributeUse> attrs, Modifiers mods)
	{
		var kw = Advance();
		var kind = kw.Kind switch
		{
			TokenKind.KwUnit => TypeKind.Unit,
			TokenKind.KwAttribute => TypeKind.Attribute,
			_ => mods.IsAbstract ? TypeKind.AbstractType : TypeKind.Type,
		};
		if (kind == TypeKind.Unit && At(TokenKind.Bang))
		{
			Advance();
			kind = TypeKind.ImmutableUnit;
		}

		var (name, nameSpan) = ExpectIdent("имя типа");
		if (name == "<missing>") return null;
		var tparams = ParseTypeParams();

		if (At(TokenKind.Eq))
		{
			Advance();
			SkipNewLines();
			var ty = ParseType();
			if (ty is not UnionType u)
			{
				Error(ty.Span, "ожидался union-тип вида [T1, T2] в правой части алиаса");
				return null;
			}
			return new UnionAliasDecl(attrs, mods, name, nameSpan, tparams, u, SpanFrom(start));
		}

		List<Param>? primary = null;
		if (At(TokenKind.LParen)) primary = ParseParams();
		var parents = At(TokenKind.LBracket) ? ParseParents() : [];

		IReadOnlyList<Decl> members = [];
		if (At(TokenKind.LBrace)) members = ParseMembers();
		return new TypeDecl(attrs, mods, kind, name, nameSpan, tparams, primary, parents, members, SpanFrom(start));
	}

	/// <summary>`[A, B(x * 2)]`; курсор на `[`.</summary>
	private List<ParentRef> ParseParents()
	{
		var list = new List<ParentRef>();
		Expect(TokenKind.LBracket, "'['");
		_ignoreNl++;
		while (!At(TokenKind.RBracket) && !AtEof)
		{
			int s = StartPos;
			var t = ParseType();
			var args = At(TokenKind.LParen) ? ParseArgList() : [];
			list.Add(new ParentRef(t, args, SpanFrom(s)));
			if (!Eat(TokenKind.Comma)) break;
		}
		Expect(TokenKind.RBracket, "']'");
		_ignoreNl--;
		return list;
	}
}
