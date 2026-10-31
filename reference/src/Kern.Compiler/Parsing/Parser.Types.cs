using Kern.Compiler.Lexing;
using Kern.Compiler.Syntax;
using Kern.Core;

namespace Kern.Compiler.Parsing;

// Разбор синтаксических ссылок на типы и generic-параметров.
public sealed partial class Parser
{
	/// <summary>Токен может начинать выражение (для диапазонов и неоднозначности `?`).</summary>
	private static bool StartsExpr(TokenKind k) => k is
		TokenKind.Ident or TokenKind.Underscore or TokenKind.Number or TokenKind.String or TokenKind.FString
		or TokenKind.Char or TokenKind.KwTrue or TokenKind.KwFalse or TokenKind.KwNil or TokenKind.KwNone
		or TokenKind.LParen or TokenKind.LBracket or TokenKind.Minus or TokenKind.Plus or TokenKind.Bang
		or TokenKind.Dollar or TokenKind.DollarBracket or TokenKind.KwIf or TokenKind.KwMatch
		or TokenKind.KwLoop or TokenKind.KwFun or TokenKind.KwAsync or TokenKind.KwAwait;

	/// <summary>Тип со следующим постфиксом: `T?`.</summary>
	private TypeRef ParseType()
	{
		int start = StartPos;
		var t = ParseTypePrimary();
		while (At(TokenKind.Question) && QuestionIsNullable())
		{
			Advance();
			t = new NullableType(t, SpanFrom(start));
		}
		return t;
	}

	/// <summary>`?` после типа — nullable, кроме случая `x is T ? a : b` (там следом идёт выражение).</summary>
	private bool QuestionIsNullable() => !_typeInExpr || !StartsExpr(Peek(1).Kind);

	private TypeRef ParseTypePrimary()
	{
		int start = StartPos;
		switch (Kind)
		{
			case TokenKind.KwFun:
			{
				Advance();
				var ps = new List<TypeRef>();
				if (At(TokenKind.LParen))
				{
					Advance();
					_ignoreNl++;
					while (!At(TokenKind.RParen) && !AtEof)
					{
						ps.Add(ParseType());
						if (!Eat(TokenKind.Comma)) break;
					}
					Expect(TokenKind.RParen, "')'");
					_ignoreNl--;
				}
				TypeRef? ret = null;
				if (At(TokenKind.Arrow))
				{
					Advance();
					ret = ParseType();
				}
				return new FuncType(ps, ret, SpanFrom(start));
			}
			case TokenKind.LParen:
			{
				Advance();
				_ignoreNl++;
				var els = new List<TupleTypeElement>();
				while (!At(TokenKind.RParen) && !AtEof)
				{
					int es = StartPos;
					var ty = ParseType();
					string? name = null;
					if (At(TokenKind.Ident)) name = Advance().Text;
					els.Add(new TupleTypeElement(ty, name, SpanFrom(es)));
					if (!Eat(TokenKind.Comma)) break;
				}
				Expect(TokenKind.RParen, "')'");
				_ignoreNl--;
				return new TupleType(els, SpanFrom(start));
			}
			case TokenKind.LBracket:
			{
				Advance();
				_ignoreNl++;
				var ms = new List<TypeRef>();
				while (!At(TokenKind.RBracket) && !AtEof)
				{
					ms.Add(ParseType());
					if (!Eat(TokenKind.Comma)) break;
				}
				Expect(TokenKind.RBracket, "']'");
				_ignoreNl--;
				return new UnionType(ms, SpanFrom(start));
			}
			case TokenKind.Ident:
			{
				var sb = new System.Text.StringBuilder(Advance().Text);
				while (At(TokenKind.Dot) && Peek(1).Kind == TokenKind.Ident)
				{
					Advance();
					sb.Append('.').Append(Advance().Text);
				}
				var args = At(TokenKind.Lt) ? ParseTypeArgs() : [];
				string name = sb.ToString();
				if (name == "array" && args.Count == 1)
					return new ArrayType(args[0], SpanFrom(start));
				return new NamedType(name, args, SpanFrom(start));
			}
			default:
				ErrorExpected("тип");
				return new NamedType("<missing>", [], new TextSpan(Cur.Span.Start, 0));
		}
	}

	/// <summary>`&lt;T1, T2&gt;` — `>>` приходит как два токена `>`.</summary>
	private List<TypeRef> ParseTypeArgs()
	{
		var list = new List<TypeRef>();
		Expect(TokenKind.Lt, "'<'");
		do
		{
			list.Add(ParseType());
		} while (Eat(TokenKind.Comma));
		Expect(TokenKind.Gt, "'>'");
		return list;
	}

	/// <summary>Тип объявления переменной: `T`, `T!`, `T!?`, `T?!`. singleAssign — был `!`.</summary>
	private TypeRef ParseDeclType(out bool singleAssign)
	{
		singleAssign = false;
		int start = StartPos;
		var t = ParseType();
		while (true)
		{
			if (At(TokenKind.Bang) && !singleAssign && Peek(1).Kind is TokenKind.Ident or TokenKind.Question)
			{
				Advance();
				singleAssign = true;
			}
			else if (singleAssign && At(TokenKind.Question))
			{
				Advance();
				t = new NullableType(t, SpanFrom(start));
			}
			else break;
		}
		return t;
	}

	/// <summary>Generic-параметры объявления: `&lt;T, V[ctor(i32), IHashable]&gt;`; пусто, если `<` нет.</summary>
	private IReadOnlyList<TypeParam> ParseTypeParams()
	{
		if (!At(TokenKind.Lt)) return [];
		Advance();
		var list = new List<TypeParam>();
		do
		{
			int start = StartPos;
			var (name, _) = ExpectIdent("имя generic-параметра");
			var cons = new List<TypeConstraint>();
			if (At(TokenKind.LBracket))
			{
				Advance();
				_ignoreNl++;
				do
				{
					int cs = StartPos;
					if (At(TokenKind.KwCtor))
					{
						Advance();
						var ps = new List<TypeRef>();
						Expect(TokenKind.LParen, "'('");
						while (!At(TokenKind.RParen) && !AtEof)
						{
							ps.Add(ParseType());
							if (!Eat(TokenKind.Comma)) break;
						}
						Expect(TokenKind.RParen, "')'");
						cons.Add(new CtorConstraint(ps, SpanFrom(cs)));
					}
					else
					{
						var ty = ParseType();
						cons.Add(new BoundConstraint(ty, SpanFrom(cs)));
					}
				} while (Eat(TokenKind.Comma));
				Expect(TokenKind.RBracket, "']'");
				_ignoreNl--;
			}
			list.Add(new TypeParam(name, cons, SpanFrom(start)));
			if (name == "<missing>") break;
		} while (Eat(TokenKind.Comma));
		Expect(TokenKind.Gt, "'>'");
		return list;
	}
}
