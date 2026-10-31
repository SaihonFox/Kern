namespace Kern.Compiler.Lexing;

/// <summary>Таблицы ключевых слов и суффиксов.</summary>
public static class TokenFacts
{
	public static readonly IReadOnlyDictionary<string, TokenKind> Keywords = new Dictionary<string, TokenKind>
	{
		["fun"] = TokenKind.KwFun, ["type"] = TokenKind.KwType, ["unit"] = TokenKind.KwUnit,
		["use"] = TokenKind.KwUse, ["loop"] = TokenKind.KwLoop, ["match"] = TokenKind.KwMatch,
		["async"] = TokenKind.KwAsync, ["await"] = TokenKind.KwAwait, ["op"] = TokenKind.KwOp,
		["ctor"] = TokenKind.KwCtor, ["open"] = TokenKind.KwOpen, ["in"] = TokenKind.KwIn,
		["is"] = TokenKind.KwIs, ["var"] = TokenKind.KwVar, ["if"] = TokenKind.KwIf,
		["else"] = TokenKind.KwElse, ["stop"] = TokenKind.KwStop, ["next"] = TokenKind.KwNext,
		["ret"] = TokenKind.KwRet,
		["pub"] = TokenKind.KwPub, ["priv"] = TokenKind.KwPriv, ["prot"] = TokenKind.KwProt,
		["int"] = TokenKind.KwInt, ["true"] = TokenKind.KwTrue, ["false"] = TokenKind.KwFalse,
		["nil"] = TokenKind.KwNil, ["none"] = TokenKind.KwNone, ["project"] = TokenKind.KwProject,
		["attribute"] = TokenKind.KwAttribute, ["enum"] = TokenKind.KwEnum,
	};

	/// <summary>Допустимые суффиксы числовых литералов (нормализованное имя типа).</summary>
	public static readonly IReadOnlySet<string> NumberSuffixes = new HashSet<string>
	{
		"i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64", "f16", "f32", "f64", "b",
	};

	/// <summary>Нормализация суффикса: `b` → `u8`.</summary>
	public static string NormalizeSuffix(string s) => s == "b" ? "u8" : s;

	/// <summary>Текст ключевого слова/оператора для сообщений (null для литералов).</summary>
	public static string? KeywordText(TokenKind kind) =>
		Keywords.FirstOrDefault(p => p.Value == kind).Key;
}
