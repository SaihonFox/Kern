using Kern.Compiler.Lexing;
using Xunit;

namespace Kern.Tests.Lexing;

public sealed class LexerTests
{
	[Fact]
	public void Lexes_project_as_a_keyword()
	{
		var result = Lexer.Lex("project geometry.shapes:");

		Assert.Empty(result.Diagnostics);
		Assert.Equal(TokenKind.Project, result.Tokens[0].Kind);
	}

	[Fact]
	public void Lexer_tokens_round_trip_to_their_source_spans()
	{
		const string source = "fun main { i32 n = 1 + 2 // comment\r\n\tprintln(n) }";
		var result = Lexer.Lex(source);

		Assert.Empty(result.Diagnostics);
		Assert.All(result.Tokens, token =>
		{
			Assert.InRange(token.Span.Start, 0, source.Length);
			Assert.InRange(token.Span.End, token.Span.Start, source.Length);
			Assert.Equal(token.Text, source.Substring(token.Span.Start, token.Span.Length));
		});
		Assert.Equal(TokenKind.EndOfFile, result.Tokens[^1].Kind);
	}

	[Fact]
	public void Lexes_keywords_identifiers_numbers_and_punctuation()
	{
		var result = Lexer.Lex("fun main { var x = 42 }");

		Assert.Empty(result.Diagnostics);
		Assert.Equal(
			[TokenKind.Fun, TokenKind.Identifier, TokenKind.LeftBrace, TokenKind.Var,
			TokenKind.Identifier, TokenKind.Equals, TokenKind.IntegerLiteral,
			TokenKind.RightBrace, TokenKind.EndOfFile],
			result.Tokens.Select(token => token.Kind));
	}


	[Fact]
	public void Lexes_radix_decimal_suffix_and_fraction_literals()
	{
		var result = Lexer.Lex("0xFF 0b101 0h17 5f64 5b .5 12.0");

		Assert.Empty(result.Diagnostics);
		Assert.Equal(
			[TokenKind.IntegerLiteral, TokenKind.IntegerLiteral, TokenKind.IntegerLiteral,
			TokenKind.NumberLiteral, TokenKind.NumberLiteral, TokenKind.NumberLiteral, TokenKind.NumberLiteral,
			TokenKind.EndOfFile],
			result.Tokens.Select(token => token.Kind));
	}

	[Fact]
	public void Lexes_numeric_separators()
	{
		var result = Lexer.Lex("1_000_000 0xFF_FF 1_0.5_0");

		Assert.Empty(result.Diagnostics);
		Assert.Equal(
			["1_000_000", "0xFF_FF", "1_0.5_0"],
			result.Tokens.Where(token => token.Kind is TokenKind.IntegerLiteral or TokenKind.NumberLiteral)
				.Select(token => token.Text));
	}

	[Fact]
	public void Skips_shebang_on_the_first_line()
	{
		const string source = "#!/usr/bin/env kern\r\nfun main { }";
		var result = Lexer.Lex(source);

		Assert.Empty(result.Diagnostics);
		Assert.Equal(TokenKind.Fun, result.Tokens[0].Kind);
		Assert.Equal(source.IndexOf("fun", StringComparison.Ordinal), result.Tokens[0].Span.Start);
	}

	[Fact]
	public void Lexes_every_number_form_and_suffix()
	{
		var result = Lexer.Lex(".5 12.0 0x012DEF 0b01101010 0h123523 5f64 5b 5i32 5u8");

		Assert.Empty(result.Diagnostics);
		Assert.Equal([".5", "12.0", "0x012DEF", "0b01101010", "0h123523", "5f64", "5b", "5i32", "5u8", ""],
			result.Tokens.Select(token => token.Text));
		Assert.Equal(6, result.Tokens.Count(token => token.Kind == TokenKind.NumberLiteral));
	}

	[Fact]
	public void Lexes_integer_member_access_without_numeric_diagnostic()
	{
		var result = Lexer.Lex("21.double");

		Assert.Empty(result.Diagnostics);
		Assert.Equal(
			[TokenKind.IntegerLiteral, TokenKind.Dot, TokenKind.Identifier, TokenKind.EndOfFile],
			result.Tokens.Select(token => token.Kind));
		Assert.Equal(["21", ".", "double", ""], result.Tokens.Select(token => token.Text));
	}

	[Fact]
	public void Reports_malformed_numbers_and_continues_lexing()
	{
		var result = Lexer.Lex("0x 0b102 0h8 12__3 4wat 9");

		Assert.Equal(5, result.Diagnostics.Count(diagnostic => diagnostic.Code == "LEX005"));
		Assert.Equal("Некорректный числовой литерал", result.Diagnostics[0].Message);
		Assert.Equal("9", result.Tokens[^2].Text);
		Assert.Equal(TokenKind.EndOfFile, result.Tokens[^1].Kind);
	}

	[Fact]
	public void Lexes_longest_match_operators()
	{
		var result = Lexer.Lex("**= === !== ** == != <= >= ?. ?: ..= .. += -= *= /= %= => ++ -- && || !in !is + -> - * / % < > ! ? = & | ^ ~");

		Assert.Empty(result.Diagnostics);
		Assert.Equal(["**=", "===", "!==", "**", "==", "!=", "<=", ">=", "?.", "?:", "..=", "..",
			"+=", "-=", "*=", "/=", "%=", "=>", "++", "--", "&&", "||", "!in", "!is", "+", "->", "-", "*", "/", "%", "<", ">", "!", "?", "=", "&", "|", "^", "~", ""],
			result.Tokens.Select(token => token.Text));
	}

	[Fact]
	public void Lexes_bitwise_operators_and_compound_assignments_longest_first()
	{
		var result = Lexer.Lex("<<= >>= & | ^ ~ << >> &= |= ^= && ||");

		Assert.Empty(result.Diagnostics);
		Assert.Equal(["<<=", ">>=", "&", "|", "^", "~", "<<", ">>", "&=", "|=", "^=", "&&", "||", ""],
			result.Tokens.Select(token => token.Text));
		Assert.All(result.Tokens.Take(13), token => Assert.Equal(TokenKind.Operator, token.Kind));
	}

	[Fact]
	public void Lexes_tilde_before_fun_as_override_marker_not_operator_run()
	{
		var result = Lexer.Lex("~fun f() { } ~x");

		Assert.Empty(result.Diagnostics);
		Assert.Equal(["~", "fun", "f", "(", ")", "{", "}", "~", "x", ""],
			result.Tokens.Select(token => token.Text));
	}

	[Fact]
	public void Lexes_all_string_prefixes_triples_and_interpolation_spellings()
	{
		var result = Lexer.Lex("f\"${expr} ${expr=} ${val:.2f}\" r\"raw\" x\"\\d+\" v\"value\" fr\"raw ${x}\" fx\"${x}\" fv\"${x}\" f\"\"\"multi\nline ${x}\"\"\"");

		Assert.Empty(result.Diagnostics);
		var interpolated = result.Tokens.Where(token => token.Kind == TokenKind.StringLiteral).Select(token => token.Text).ToArray();
		Assert.Equal(8, interpolated.Length);
		Assert.Equal("f\"${expr} ${expr=} ${val:.2f}\"", interpolated[0]);
		Assert.Equal("f\"\"\"multi\nline ${x}\"\"\"", interpolated[^1]);
	}

	[Fact]
	public void Lexes_verbatim_string_with_escaped_quotes()
	{
		var result = Lexer.Lex("v\"он сказал \"\"привет\"\"\"");

		Assert.Empty(result.Diagnostics);
		Assert.Single(result.Tokens, token => token.Kind == TokenKind.StringLiteral);
	}

	[Fact]
	public void Lexes_interpolation_with_nested_quoted_call_arguments()
	{
		var result = Lexer.Lex("f\"таблица: ${t.attribute(\"entity\")?.args[0]}\" f\"a=${freq.get(\"a\")}\"");

		Assert.Empty(result.Diagnostics);
		Assert.Equal(2, result.Tokens.Count(token => token.Kind == TokenKind.StringLiteral));
	}

	[Fact]
	public void Ignores_braces_and_escaped_quotes_inside_interpolation_strings()
	{
		var result = Lexer.Lex("f\"${call(\"{ } \\\"quoted\\\"\")} done\"");

		Assert.Empty(result.Diagnostics);
		Assert.Single(result.Tokens, token => token.Kind == TokenKind.StringLiteral);
	}

	[Fact]
	public void Ignores_braces_in_interpolation_comments()
	{
		var result = Lexer.Lex("f\"${value /* } /* nested { */ */ + other} ok\"");

		Assert.Empty(result.Diagnostics);
		Assert.Single(result.Tokens, token => token.Kind == TokenKind.StringLiteral);
	}

	[Fact]
	public void Reports_unmatched_interpolation_and_recovers_at_end_of_input()
	{
		var interpolation = Lexer.Lex("f\"${call(\"value\")}");
		var nestedString = Lexer.Lex("f\"${call(\"value)}\"");

		Assert.Contains(interpolation.Diagnostics, diagnostic => diagnostic.Code == "LEX003");
		Assert.Contains(nestedString.Diagnostics, diagnostic => diagnostic.Code == "LEX003");
		Assert.Equal(TokenKind.EndOfFile, interpolation.Tokens[^1].Kind);
		Assert.Equal(TokenKind.EndOfFile, nestedString.Tokens[^1].Kind);
	}

	[Fact]
	public void Lexes_line_and_nested_block_comments()
	{
		var result = Lexer.Lex("left // line comment\n/* outer /* nested */ end */ right");

		Assert.Empty(result.Diagnostics);
		Assert.Equal(["left", "right", ""], result.Tokens.Select(token => token.Text));
	}

	[Fact]
	public void Lexes_nested_multiline_comments()
	{
		var result = Lexer.Lex("x /* outer /* inner */ outer */ y");

		Assert.Empty(result.Diagnostics);
		Assert.Equal(["x", "y", ""], result.Tokens.Select(token => token.Text));
	}

	[Fact]
	public void Reports_unterminated_nested_multiline_comment()
	{
		var result = Lexer.Lex("x /* outer /* inner */");

		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "LEX002");
		Assert.Equal(TokenKind.EndOfFile, result.Tokens[^1].Kind);
	}

	[Fact]
	public void Lexes_arrow_and_string_prefixes()
	{
		var result = Lexer.Lex("f\"a\" r\"b\" x\"c\" v\"d\" fr\"e\" fx\"f\" fv\"g\" ->");

		Assert.Empty(result.Diagnostics);
		Assert.Equal(7, result.Tokens.Count(token => token.Kind == TokenKind.StringLiteral));
		Assert.Equal("->", result.Tokens[^2].Text);
	}

	[Fact]
	public void Lexes_character_literals_and_reports_invalid_character_literals()
	{
		var valid = Lexer.Lex("'k' '\\n' '\\u4564'");
		var invalid = Lexer.Lex("'' '\\u12G4'");

		Assert.Empty(valid.Diagnostics);
		Assert.Equal(3, valid.Tokens.Count(token => token.Kind == TokenKind.CharLiteral));
		Assert.DoesNotContain(valid.Tokens, token => token.Kind == TokenKind.StringLiteral);
		Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Code == "LEX004");
	}

	[Fact]
	public void Reports_unknown_characters_and_keeps_scanning()
	{
		var result = Lexer.Lex("x § y");

		Assert.Single(result.Diagnostics);
		Assert.Equal("LEX001", result.Diagnostics[0].Code);
		Assert.Equal(TokenKind.EndOfFile, result.Tokens[^1].Kind);
	}

	[Fact]
	public void Reports_unclosed_comment_and_string()
	{
		var result = Lexer.Lex("x /* comment");
		var stringResult = Lexer.Lex("\"text");

		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "LEX002");
		Assert.Contains(stringResult.Diagnostics, diagnostic => diagnostic.Code == "LEX003");
		Assert.Equal(TokenKind.EndOfFile, result.Tokens[^1].Kind);
	}
}
