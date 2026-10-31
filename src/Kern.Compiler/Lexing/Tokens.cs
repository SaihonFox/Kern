using Kern.Core.Diagnostics;

namespace Kern.Compiler.Lexing;

public enum TokenKind
{
	EndOfFile,
	Identifier,
	IntegerLiteral,
	NumberLiteral,
	StringLiteral,
	CharLiteral,
	Operator,
	Fun,
	Type,
	Unit,
	Use,
	Loop,
	Match,
	Async,
	Await,
	Op,
	Ctor,
	Open,
	In,
	Is,
	Var,
	Else,
	Ret,
	Stop,
	Next,
	Project,
	At,
	Hash,
	Dollar,
	LeftParen,
	RightParen,
	LeftBrace,
	RightBrace,
	LeftBracket,
	RightBracket,
	Comma,
	Colon,
	Dot,
	LeadingDot,
	Semicolon,
	Equals,
	Unknown
}

public readonly record struct Token(TokenKind Kind, string Text, TextSpan Span);
