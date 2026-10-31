using System.Numerics;
using Kern.Core;

namespace Kern.Compiler.Lexing;

/// <summary>
/// Токен. Value: для Number — <see cref="NumberValue"/>, для String — string,
/// для Char — int (кодовая точка), для FString — IReadOnlyList&lt;FStringPart&gt;.
/// </summary>
public sealed record Token(TokenKind Kind, string Text, TextSpan Span, object? Value = null)
{
	public NumberValue Number => (NumberValue)Value!;
	public string StringValue => (string)Value!;
	public int CharValue => (int)Value!;
	public IReadOnlyList<FStringPart> Parts => (IReadOnlyList<FStringPart>)Value!;

	public override string ToString() => $"{Kind} '{Text}' @{Span.Start}";
}

/// <summary>Числовой литерал. Suffix — точный тип (`u32`, `f64`, `b` → "u8" нормализуется лексером).</summary>
public sealed record NumberValue(BigInteger Integer, double Float, bool IsFloat, string? Suffix);

/// <summary>Часть интерполированной строки.</summary>
public abstract record FStringPart(TextSpan Span);

/// <summary>Литеральный текст (escape уже обработаны).</summary>
public sealed record FStringText(string Text, TextSpan Span) : FStringPart(Span);

/// <summary>
/// Вложенное выражение `${expr}`. Tokens — под-токены выражения (заканчиваются Eof).
/// Debug — форма `${expr=}`; Format — спецификатор после `:` (без двоеточия), если есть.
/// </summary>
public sealed record FStringExpr(
	IReadOnlyList<Token> Tokens, string SourceText, bool Debug, string? Format, TextSpan Span) : FStringPart(Span);
