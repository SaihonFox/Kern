using Kern.Compiler.Lexing;
using Kern.Compiler.Syntax;
using Kern.Core;

namespace Kern.Compiler.Parsing;

// Члены типов: тело типа, поля, свойства (аксессоры), расширения свойств, enum.
public sealed partial class Parser
{
	/// <summary>`{ member* }`; курсор на `{`.</summary>
	private List<Decl> ParseMembers()
	{
		var list = new List<Decl>();
		Expect(TokenKind.LBrace, "'{'");
		int saved = EnterBlock();
		ParseMemberList(list);
		Expect(TokenKind.RBrace, "'}'");
		ExitBlock(saved);
		return list;
	}

	/// <summary>Члены до `}` (не потребляется). Ошибки восстанавливаются по концу строки.</summary>
	private void ParseMemberList(List<Decl> list)
	{
		while (true)
		{
			SkipSeparators();
			if (At(TokenKind.RBrace) || AtEof) return;
			int before = _p;
			var d = ParseDecl(topLevel: false);
			if (d is not null) list.Add(d);
			else SyncMember();
			if (_p == before) Advance();
			if (!AtSeparatorOrEnd && _t[_p - 1].Kind != TokenKind.RBrace)
			{
				ErrorExpected("конец члена (перевод строки или ';')");
				SyncMember();
			}
		}
	}

	// ---------------- Поля и свойства ----------------

	/// <summary>
	/// `T x [= e]`, `var x = e`, `T x { get; set }`, `T x get expr`, `T $[A].x`,
	/// расширение свойства `T Recv.name body`.
	/// </summary>
	private Decl? ParseFieldOrProperty(int start, List<AttributeUse> attrs, Modifiers mods)
	{
		TypeRef? type = null;
		if (At(TokenKind.KwVar)) Advance();
		else if (Kind is TokenKind.Ident or TokenKind.LParen or TokenKind.LBracket or TokenKind.KwFun)
			type = ParseType();
		else
		{
			ErrorExpected("объявление");
			return null;
		}

		string? parentSel = null;
		string name;
		TextSpan nameSpan;

		if (At(TokenKind.DollarBracket))
		{
			Advance();
			var (p, _) = ExpectIdent("имя родителя");
			Expect(TokenKind.RBracket, "']'");
			Expect(TokenKind.Dot, "'.'");
			parentSel = p;
			(name, nameSpan) = ExpectMemberName("имя члена");
		}
		else if (Kind == TokenKind.Ident && Peek(1).Kind is TokenKind.Dot or TokenKind.Lt)
		{
			// Расширение свойства: `i32 str.len2 body`.
			ParseReceiverAndName(out var receiver, out name, out nameSpan);
			if (type is null || name == "<missing>")
			{
				if (type is null) Error(nameSpan, "у расширения свойства должен быть явный тип");
				return null;
			}
			var body = ParseBody();
			return new ExtensionPropDecl(attrs, mods, type, receiver, name, nameSpan, body, SpanFrom(start));
		}
		else
		{
			(name, nameSpan) = ExpectMemberName("имя");
		}
		if (name == "<missing>") return null;

		// Свойство только для чтения: `i32 b => a`
		if (At(TokenKind.FatArrow))
		{
			if (type is null)
			{
				Error(nameSpan, "свойство должно иметь явный тип (var недопустим)");
				return null;
			}
			int arrowStart = StartPos;
			Advance();
			SkipNewLines();
			var getBody = ParseExpr();
			var getter = new Accessor(AccessorKind.Get, Access.Default, null, getBody, SpanFrom(arrowStart));
			return new PropertyDecl(attrs, mods, type, name, nameSpan, parentSel, [getter], null, SpanFrom(start));
		}

		// Свойство с аксессорами.
		bool singleAccessor = Kind == TokenKind.Ident && Cur.Text is "get" or "set" or "init";
		if (At(TokenKind.LBrace) || singleAccessor)
		{
			if (type is null)
			{
				Error(nameSpan, "свойство должно иметь явный тип (var недопустим)");
				return null;
			}
			var accessors = new List<Accessor>();
			if (singleAccessor)
			{
				var a = ParseAccessor();
				if (a is null) return null;
				accessors.Add(a);
			}
			else ParseAccessorBlock(accessors);

			Expr? pinit = null;
			if (At(TokenKind.Eq))
			{
				Advance();
				SkipNewLines();
				pinit = ParseExpr();
			}
			return new PropertyDecl(attrs, mods, type, name, nameSpan, parentSel, accessors, pinit, SpanFrom(start));
		}

		Expr? init = null;
		if (At(TokenKind.Eq))
		{
			Advance();
			SkipNewLines();
			init = ParseExpr();
		}
		return new FieldDecl(attrs, mods, type, name, nameSpan, parentSel, init, SpanFrom(start));
	}

	/// <summary>`{ get; set }`, `{ get expr; set(value) stmt }`; курсор на `{`.</summary>
	private void ParseAccessorBlock(List<Accessor> list)
	{
		Expect(TokenKind.LBrace, "'{'");
		int saved = EnterBlock();
		while (true)
		{
			while (Kind is TokenKind.NewLine or TokenKind.Semicolon or TokenKind.Comma) Advance();
			if (At(TokenKind.RBrace) || AtEof) break;
			int before = _p;
			var a = ParseAccessor();
			if (a is not null) list.Add(a);
			else SyncMember();
			if (_p == before) Advance();
			if (!AtSeparatorOrEnd && !At(TokenKind.Comma) && _t[_p - 1].Kind != TokenKind.RBrace)
			{
				ErrorExpected("конец аксессора");
				SyncMember();
			}
		}
		Expect(TokenKind.RBrace, "'}'");
		ExitBlock(saved);
	}

	private Accessor? ParseAccessor()
	{
		int s = StartPos;
		var access = Access.Default;
		switch (Kind)
		{
			case TokenKind.KwPub: access = Access.Pub; Advance(); break;
			case TokenKind.KwPriv: access = Access.Priv; Advance(); break;
			case TokenKind.KwProt: access = Access.Prot; Advance(); break;
			case TokenKind.KwInt: access = Access.Int; Advance(); break;
		}

		if (Kind != TokenKind.Ident || Cur.Text is not ("get" or "set" or "init"))
		{
			ErrorExpected("get, set или init");
			return null;
		}
		var kind = Advance().Text switch
		{
			"get" => AccessorKind.Get,
			"set" => AccessorKind.Set,
			_ => AccessorKind.Init,
		};

		string? pname = null;
		// `set(value) ...` — имя параметра; для `get (expr)` скобки — часть выражения.
		if (kind != AccessorKind.Get && At(TokenKind.LParen)
			&& Peek(1).Kind == TokenKind.Ident && Peek(2).Kind == TokenKind.RParen)
		{
			Advance();
			pname = Advance().Text;
			Advance();
		}

		Expr? body = null;
		if (!AtSeparatorOrEnd && !At(TokenKind.Comma))
			body = ParseBranch();
		return new Accessor(kind, access, pname, body, SpanFrom(s));
	}

	// ---------------- enum ----------------

	/// <summary>`enum N(params)[Parents] { red, ok(200), green = 5; members }`.</summary>
	private Decl? ParseEnum(int start, List<AttributeUse> attrs, Modifiers mods)
	{
		Advance(); // enum
		var (name, nameSpan) = ExpectIdent("имя enum");
		if (name == "<missing>") return null;

		List<Param>? primary = null;
		if (At(TokenKind.LParen)) primary = ParseParams();
		var parents = At(TokenKind.LBracket) ? ParseParents() : [];

		var entries = new List<EnumEntry>();
		var members = new List<Decl>();
		if (At(TokenKind.LBrace))
		{
			Advance();
			int saved = EnterBlock();

			while (true)
			{
				while (Kind is TokenKind.NewLine or TokenKind.Comma) Advance();
				if (Kind == TokenKind.Ident && IsEnumEntryAhead()) entries.Add(ParseEnumEntry());
				else break;
			}
			if (At(TokenKind.Semicolon)) Advance();
			ParseMemberList(members);

			Expect(TokenKind.RBrace, "'}'");
			ExitBlock(saved);
		}
		return new EnumDecl(attrs, mods, name, nameSpan, primary, parents, entries, members, SpanFrom(start));
	}

	/// <summary>Ident, за которым `,` / конец строки / `(` / `=` / `;` / `}` — элемент enum, иначе уже член.</summary>
	private bool IsEnumEntryAhead() =>
		Peek(1).Kind is TokenKind.Comma or TokenKind.NewLine or TokenKind.LParen or TokenKind.Eq
			or TokenKind.Semicolon or TokenKind.RBrace;

	private EnumEntry ParseEnumEntry()
	{
		int s = StartPos;
		var (name, nameSpan) = ExpectIdent("имя элемента enum");
		var args = At(TokenKind.LParen) ? ParseArgList() : [];
		Expr? value = null;
		if (At(TokenKind.Eq))
		{
			Advance();
			value = ParseExpr();
		}
		return new EnumEntry(name, nameSpan, args, value, SpanFrom(s));
	}
}
