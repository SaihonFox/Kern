using System.Globalization;
using System.Text;

namespace Kern.Bytecode;

/// <summary>Вид константы в <see cref="KbcModule.ConstPool"/>.</summary>
public enum KConstKind : byte
{
	Nil = 0,
	None,
	Void,
	Bool,
	I64,
	F64,
	Char,
	Str,
}

/// <summary>
/// Константа пула. Числа хранятся в <see cref="Bits"/> (f64 — как IEEE-биты),
/// строки и прочие ссылки — в <see cref="Ref"/>. i32-литералы кодируются как i64.
/// </summary>
public readonly struct KConst : IEquatable<KConst>
{
	public KConstKind Kind { get; }
	public long Bits { get; }
	public object? Ref { get; }

	private KConst(KConstKind kind, long bits, object? reference = null)
	{
		Kind = kind;
		Bits = bits;
		Ref = reference;
	}

	public static readonly KConst Nil = new(KConstKind.Nil, 0);
	public static readonly KConst None = new(KConstKind.None, 0);
	public static readonly KConst Void = new(KConstKind.Void, 0);
	public static KConst Bool(bool value) => new(KConstKind.Bool, value ? 1 : 0);
	public static KConst I64(long value) => new(KConstKind.I64, value);
	public static KConst F64(double value) => new(KConstKind.F64, BitConverter.DoubleToInt64Bits(value));
	public static KConst Char(char value) => new(KConstKind.Char, value);
	public static KConst Str(string value) => new(KConstKind.Str, 0, value ?? throw new ArgumentNullException(nameof(value)));

	public bool AsBool => Bits != 0;
	public char AsChar => (char)(ushort)Bits;
	public long AsI64 => Bits;
	public double AsF64 => BitConverter.Int64BitsToDouble(Bits);
	public string AsStr => (string)Ref!;

	/// <summary>Текст для дизассемблера и сообщений: <c>i64 42</c>, <c>str "a"</c>, <c>nil</c>.</summary>
	public string Describe() => Kind switch
	{
		KConstKind.Nil => "nil",
		KConstKind.None => "none",
		KConstKind.Void => "void",
		KConstKind.Bool => AsBool ? "bool true" : "bool false",
		KConstKind.I64 => $"i64 {Bits.ToString(CultureInfo.InvariantCulture)}",
		KConstKind.F64 => $"f64 {AsF64.ToString("R", CultureInfo.InvariantCulture)}",
		KConstKind.Char => $"char {KbcText.EscapeChar(AsChar)}",
		KConstKind.Str => $"str {KbcText.Escape(AsStr)}",
		_ => "?",
	};

	public bool Equals(KConst other) =>
		Kind == other.Kind && Bits == other.Bits && Equals(Ref, other.Ref);

	public override bool Equals(object? obj) => obj is KConst other && Equals(other);

	public override int GetHashCode() => HashCode.Combine(Kind, Bits, Ref);
}

/// <summary>Экранирование строк/символов для текстовых форм (.kbc-дизассемблер, сообщения).</summary>
internal static class KbcText
{
	public static string Escape(string value)
	{
		StringBuilder sb = new(value.Length + 2);
		sb.Append('"');
		foreach (char c in value) AppendEscaped(sb, c, '"');
		sb.Append('"');
		return sb.ToString();
	}

	public static string EscapeChar(char value)
	{
		StringBuilder sb = new(4);
		sb.Append('\'');
		AppendEscaped(sb, value, '\'');
		sb.Append('\'');
		return sb.ToString();
	}

	private static void AppendEscaped(StringBuilder sb, char c, char quote)
	{
		switch (c)
		{
			case '\\': sb.Append("\\\\"); break;
			case '\n': sb.Append("\\n"); break;
			case '\r': sb.Append("\\r"); break;
			case '\t': sb.Append("\\t"); break;
			case '\0': sb.Append("\\0"); break;
			default:
				if (c == quote) sb.Append('\\').Append(c);
				else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
				else sb.Append(c);
				break;
		}
	}
}
