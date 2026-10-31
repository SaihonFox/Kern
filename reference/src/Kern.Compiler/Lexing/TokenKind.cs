namespace Kern.Compiler.Lexing;

/// <summary>Вид токена.</summary>
public enum TokenKind
{
	// Служебные
	Eof,
	NewLine,
	Error,

	// Литералы и идентификаторы
	Ident,
	Number,
	String,
	FString,   // f"..." — Value содержит список частей (FStringPart)
	Char,
	Underscore, // одиночный `_`

	// Ключевые слова
	KwFun, KwType, KwUnit, KwUse, KwLoop, KwMatch, KwAsync, KwAwait, KwOp, KwCtor,
	KwOpen, KwIn, KwIs, KwVar, KwIf, KwElse, KwStop, KwNext, KwRet,
	KwPub, KwPriv, KwProt, KwInt,
	KwTrue, KwFalse, KwNil, KwNone,
	KwProject, KwAttribute, KwEnum,

	// Арифметика
	Plus, Minus, Star, StarStar, Slash, Percent, Caret,

	// Сравнение / логика
	EqEq, BangEq, EqEqEq, BangEqEq,
	Lt, Gt, LtEq, GtEq,
	AmpAmp, PipePipe, Bang,
	NotIn, // !in
	NotIs, // !is

	// Null-safety и тернарный
	Question,      // ?
	QuestionColon, // ?:  (elvis)
	QuestionDot,   // ?.

	// Диапазоны, стрелка
	DotDot, DotDotEq, Arrow, FatArrow,

	// Присваивание
	Eq, PlusEq, MinusEq, StarEq, StarStarEq, SlashEq, PercentEq, CaretEq,

	// Пунктуация
	Colon, ColonColon, Semicolon, Comma, Dot,
	LParen, RParen, LBracket, RBracket, LBrace, RBrace,
	Dollar,         // $
	DollarBracket,  // $[
	Hash,           // #
	HashBracket,    // #[
	At,             // @
	Tilde,          // ~
}
