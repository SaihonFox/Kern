using System.Globalization;
using System.Numerics;
using System.Text;
using Kern.Compiler.Semantics.Types.Conversions;

namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Разобранный числовой литерал Kern: <c>0x1F</c>, <c>0b101</c>, <c>0h17</c>, <c>.5</c>, <c>1.5e3</c>,
/// <c>5f64</c>, <c>300u8</c>. <see cref="ExactType"/> задан только суффиксом. Правило константных
/// преобразований — spec §15 и DECISIONS §C: литерал без суффикса приводится к числовому типу,
/// если значение помещается; литерал с суффиксом имеет точный тип.
/// </summary>
public sealed record NumericLiteralValue
{
	private NumericLiteralValue(bool isFloat, BigInteger integerValue, double floatValue, PrimitiveKind? exactType)
	{
		IsFloat = isFloat;
		IntegerValue = integerValue;
		FloatValue = floatValue;
		ExactType = exactType;
	}

	/// <summary>Литерал содержит дробную часть, экспоненту или суффикс плавающего типа.</summary>
	public bool IsFloat { get; }

	/// <summary>Целое значение; для вещественного литерала всегда 0.</summary>
	public BigInteger IntegerValue { get; }

	/// <summary>Вещественное значение; для целого литерала — приближение <see cref="IntegerValue"/>.</summary>
	public double FloatValue { get; }

	/// <summary>Точный тип из суффикса (<c>b</c>, <c>f32</c>, <c>i64</c>, …) либо null без суффикса.</summary>
	public PrimitiveKind? ExactType { get; }

	/// <summary>Создаёт целый литерал с явным значением (в том числе отрицательным).</summary>
	public static NumericLiteralValue FromInteger(BigInteger value, PrimitiveKind? exactType = null) =>
		new(false, value, (double)value, exactType);

	/// <summary>Создаёт вещественный литерал с явным значением.</summary>
	public static NumericLiteralValue FromFloat(double value, PrimitiveKind? exactType = null) =>
		new(true, BigInteger.Zero, value, exactType);

	/// <summary>
	/// Разбирает текст литерала. Допускает необязательный знак (binder складывает унарный минус),
	/// префиксы <c>0x</c>/<c>0b</c>/<c>0h</c>, дробную часть, экспоненту, суффикс и <c>_</c>-разделители.
	/// Некорректный текст или значение, не помещающееся в тип суффикса, даёт <c>false</c> без исключений.
	/// </summary>
	public static bool TryParse(string? text, out NumericLiteralValue value)
	{
		value = null!;
		if (string.IsNullOrEmpty(text))
			return false;

		var span = text.AsSpan();
		var negative = false;
		if (span[0] is '+' or '-')
		{
			negative = span[0] == '-';
			span = span[1..];
		}

		if (span.IsEmpty)
			return false;

		return IsRadixLiteral(span)
			? TryParseRadix(span, negative, out value)
			: TryParseDecimal(span, negative, out value);
	}

	private static bool IsRadixLiteral(ReadOnlySpan<char> span) =>
		span.Length >= 2 && span[0] == '0' && span[1] is 'x' or 'X' or 'b' or 'B' or 'h' or 'H';

	private static bool TryParseRadix(ReadOnlySpan<char> span, bool negative, out NumericLiteralValue value)
	{
		value = null!;
		var radix = span[1] switch
		{
			'x' or 'X' => 16,
			'b' or 'B' => 2,
			_ => 8
		};

		var body = span[2..];
		var digitsEnd = 0;
		while (digitsEnd < body.Length && (IsDigitOfRadix(body[digitsEnd], radix) || body[digitsEnd] == '_'))
			digitsEnd++;

		if (digitsEnd == 0)
			return false;

		var digits = body[..digitsEnd];
		if (!IsValidDigitGroup(digits) || !TryParseRadixInteger(digits, radix, out var magnitude))
			return false;

		var integer = negative ? -magnitude : magnitude;
		var suffix = body[digitsEnd..];
		if (!TryParseSuffix(suffix, out var exactType, out var suffixIsFloat))
			return false;

		if (suffixIsFloat)
		{
			var floatValue = (double)integer;
			if (!NumericRules.FitsFloat(floatValue, exactType!.Value))
				return false;

			value = new NumericLiteralValue(true, BigInteger.Zero, floatValue, exactType);
			return true;
		}

		if (exactType is { } exact && !NumericRules.FitsInteger(integer, exact))
			return false;

		value = FromInteger(integer, exactType);
		return true;
	}

	private static bool TryParseDecimal(ReadOnlySpan<char> span, bool negative, out NumericLiteralValue value)
	{
		value = null!;
		var index = 0;
		while (index < span.Length && (IsAsciiDigit(span[index]) || span[index] == '_'))
			index++;

		var integerPart = span[..index];
		var hasIntegerDigits = integerPart.Length > 0;

		var hasDot = false;
		var hasFractionDigits = false;
		ReadOnlySpan<char> fractionPart = default;
		if (index < span.Length && span[index] == '.')
		{
			hasDot = true;
			index++;
			var fractionStart = index;
			while (index < span.Length && (IsAsciiDigit(span[index]) || span[index] == '_'))
				index++;
			fractionPart = span[fractionStart..index];
			hasFractionDigits = fractionPart.Length > 0;
			if (!hasFractionDigits)
				return false;
		}

		if (!hasIntegerDigits && !hasFractionDigits)
			return false;

		var hasExponent = false;
		var exponentNegative = false;
		ReadOnlySpan<char> exponentPart = default;
		if (index < span.Length && span[index] is 'e' or 'E')
		{
			hasExponent = true;
			index++;
			if (index < span.Length && span[index] is '+' or '-')
			{
				exponentNegative = span[index] == '-';
				index++;
			}

			var exponentStart = index;
			while (index < span.Length && (IsAsciiDigit(span[index]) || span[index] == '_'))
				index++;
			exponentPart = span[exponentStart..index];
			if (exponentPart.Length == 0)
				return false;
		}

		var suffix = span[index..];
		if (!TryParseSuffix(suffix, out var exactType, out var suffixIsFloat))
			return false;

		// «5f64» — вещественный литерал без дробной части; «1.5i32» и «1e3u8» недопустимы.
		if (suffixIsFloat is false && exactType is not null && (hasDot || hasExponent))
			return false;

		if (!IsValidDigitGroup(integerPart) || !IsValidDigitGroup(fractionPart) || !IsValidDigitGroup(exponentPart))
			return false;

		var isFloat = hasDot || hasExponent || suffixIsFloat;
		if (isFloat)
		{
			var floatText = BuildFloatText(integerPart, fractionPart, exponentPart, exponentNegative, negative);
			if (!double.TryParse(floatText, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatValue))
				return false;
			if (!double.IsFinite(floatValue))
				return false;
			if (exactType is { } floatExact && !NumericRules.FitsFloat(floatValue, floatExact))
				return false;

			value = new NumericLiteralValue(true, BigInteger.Zero, floatValue, exactType);
			return true;
		}

		var integerText = RemoveUnderscores(integerPart);
		if (integerText.Length == 0)
			integerText = "0";
		if (!BigInteger.TryParse(integerText, NumberStyles.None, CultureInfo.InvariantCulture, out var integer))
			return false;

		if (negative)
			integer = -integer;
		if (exactType is { } integerExact && !NumericRules.FitsInteger(integer, integerExact))
			return false;

		value = FromInteger(integer, exactType);
		return true;
	}

	private static string BuildFloatText(
		ReadOnlySpan<char> integerPart,
		ReadOnlySpan<char> fractionPart,
		ReadOnlySpan<char> exponentPart,
		bool exponentNegative,
		bool negative)
	{
		var builder = new StringBuilder();
		if (negative)
			builder.Append('-');

		builder.Append(integerPart.Length == 0 ? "0" : RemoveUnderscores(integerPart));
		builder.Append('.');
		builder.Append(fractionPart.Length == 0 ? "0" : RemoveUnderscores(fractionPart));
		if (exponentPart.Length > 0)
		{
			builder.Append('e');
			if (exponentNegative)
				builder.Append('-');
			builder.Append(RemoveUnderscores(exponentPart));
		}

		return builder.ToString();
	}

	private static bool TryParseSuffix(ReadOnlySpan<char> suffix, out PrimitiveKind? exactType, out bool isFloat)
	{
		exactType = null;
		isFloat = false;
		if (suffix.IsEmpty)
			return true;

		switch (suffix.ToString())
		{
			case "b":
				exactType = PrimitiveKind.U8;
				return true;
			case "f16":
				exactType = PrimitiveKind.F16;
				isFloat = true;
				return true;
			case "f32":
				exactType = PrimitiveKind.F32;
				isFloat = true;
				return true;
			case "f64":
				exactType = PrimitiveKind.F64;
				isFloat = true;
				return true;
			case "i8":
				exactType = PrimitiveKind.I8;
				return true;
			case "i16":
				exactType = PrimitiveKind.I16;
				return true;
			case "i32":
				exactType = PrimitiveKind.I32;
				return true;
			case "i64":
				exactType = PrimitiveKind.I64;
				return true;
			case "u8":
				exactType = PrimitiveKind.U8;
				return true;
			case "u16":
				exactType = PrimitiveKind.U16;
				return true;
			case "u32":
				exactType = PrimitiveKind.U32;
				return true;
			case "u64":
				exactType = PrimitiveKind.U64;
				return true;
			default:
				return false;
		}
	}

	private static bool TryParseRadixInteger(ReadOnlySpan<char> digits, int radix, out BigInteger value)
	{
		value = BigInteger.Zero;
		foreach (var digit in digits)
		{
			if (digit == '_')
				continue;

			var number = DigitValue(digit);
			if (number < 0 || number >= radix)
				return false;

			value = value * radix + number;
		}
		return true;
	}

	private static bool IsValidDigitGroup(ReadOnlySpan<char> group)
	{
		if (group.IsEmpty)
			return true;
		if (group[0] == '_' || group[^1] == '_')
			return false;
		for (var index = 1; index < group.Length; index++)
			if (group[index] == '_' && group[index - 1] == '_')
				return false;

		return true;
	}

	private static string RemoveUnderscores(ReadOnlySpan<char> text) =>
		text.Contains('_') ? text.ToString().Replace("_", "") : text.ToString();

	private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

	private static int DigitValue(char value) => value switch
	{
		>= '0' and <= '9' => value - '0',
		>= 'a' and <= 'f' => value - 'a' + 10,
		>= 'A' and <= 'F' => value - 'A' + 10,
		_ => -1
	};

	private static bool IsDigitOfRadix(char value, int radix)
	{
		var digit = DigitValue(value);
		return digit >= 0 && digit < radix;
	}
}
