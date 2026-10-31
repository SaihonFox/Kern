using System.Globalization;
using System.Text;
using Kern.Bytecode;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/// <summary>Форматирование `${v:спец}` и приведения `as`.</summary>
public sealed partial class Interpreter
{
	/// <summary>
	/// Спецификатор: <c>[[заполнитель]выравнивание][знак][0][ширина][,][.точность][тип]</c>,
	/// выравнивание &lt; &gt; ^, знак + или пробел, тип d x X b o e E f F g n % s.
	/// </summary>
	/// <summary>Строковое представление значения (как в `f"${v}"`).</summary>
	public string Stringify(KValue v) => FormatValue(v, "");

	internal string FormatValue(KValue v, string spec)
	{
		if (spec.Length == 0) return ValueOps.ToStr(v);
		int i = 0;
		char fill = ' ', align = '\0';
		if (spec.Length >= 2 && spec[1] is '<' or '>' or '^') { fill = spec[0]; align = spec[1]; i = 2; }
		else if (spec[0] is '<' or '>' or '^') { align = spec[0]; i = 1; }
		char sign = '-';
		if (i < spec.Length && spec[i] is '+' or ' ') sign = spec[i++];
		bool alt = i < spec.Length && spec[i] == '#' && ++i > 0;
		bool zero = i < spec.Length && spec[i] == '0' && ++i > 0;
		int width = 0;
		while (i < spec.Length && char.IsDigit(spec[i])) width = width * 10 + (spec[i++] - '0');
		bool group = i < spec.Length && spec[i] == ',' && ++i > 0;
		int prec = -1;
		if (i < spec.Length && spec[i] == '.')
		{
			i++; prec = 0;
			while (i < spec.Length && char.IsDigit(spec[i])) prec = prec * 10 + (spec[i++] - '0');
		}
		char type = i < spec.Length ? spec[i++] : '\0';
		if (i < spec.Length) throw new KernPanic($"неверный спецификатор формата '{spec}'");

		string body;
		bool numeric = v.IsNumber;
		var inv = CultureInfo.InvariantCulture;
		if (numeric)
		{
			body = FormatNumber(v, type, prec, group, alt, inv);
			if (sign != '-' && !body.StartsWith('-')) body = sign + body;
		}
		else
		{
			body = ValueOps.ToStr(v);
			if (type is not ('\0' or 's')) throw new KernPanic($"спецификатор '{type}' применим только к числам");
			if (prec >= 0 && body.Length > prec) body = body[..prec];
		}
		if (body.Length >= width) return body;
		int pad = width - body.Length;
		if (zero && numeric && align == '\0')
		{
			int signLen = body.Length > 0 && body[0] is '-' or '+' or ' ' ? 1 : 0;
			return body[..signLen] + new string('0', pad) + body[signLen..];
		}
		if (align == '\0') align = numeric ? '>' : '<';
		return align switch
		{
			'<' => body + new string(fill, pad),
			'^' => new string(fill, pad / 2) + body + new string(fill, pad - pad / 2),
			_ => new string(fill, pad) + body,
		};
	}

	private static string FormatNumber(KValue v, char type, int prec, bool group, bool alt, CultureInfo inv)
	{
		if (v.IsInteger && type is '\0' or 'd' or 'n')
		{
			string s = v.IsUnsigned ? v.AsU64.ToString(inv) : v.Bits.ToString(inv);
			if (group || type == 'n') s = (v.IsUnsigned ? v.AsU64.ToString("N0", inv) : v.Bits.ToString("N0", inv));
			return s;
		}
		if (v.IsInteger && type is 'x' or 'X' or 'b' or 'o')
		{
			ulong u = v.IsUnsigned ? v.AsU64 : unchecked((ulong)v.Bits) & (Num.BitsOf(v.Kind) == 64 ? ulong.MaxValue : (1UL << Num.BitsOf(v.Kind)) - 1);
			string body = type switch
			{
				'x' => u.ToString("x", inv), 'X' => u.ToString("X", inv),
				'b' => Convert.ToString((long)u, 2), _ => Convert.ToString((long)u, 8),
			};
			return alt ? (type switch { 'x' => "0x", 'X' => "0X", 'b' => "0b", _ => "0o" }) + body : body;
		}
		double d = v.AsF64;
		string p = prec >= 0 ? prec.ToString(inv) : "";
		return type switch
		{
			'f' or 'F' => d.ToString((group ? "N" : "F") + (prec >= 0 ? p : "6"), inv),
			'e' => d.ToString("e" + (prec >= 0 ? p : "6"), inv),
			'E' => d.ToString("E" + (prec >= 0 ? p : "6"), inv),
			'g' => d.ToString("G" + p, inv),
			'%' => (d * 100).ToString("F" + (prec >= 0 ? p : "6"), inv) + "%",
			'n' => d.ToString("N" + (prec >= 0 ? p : "2"), inv),
			'\0' => prec >= 0 ? d.ToString("F" + p, inv) : Num.ToStr(v),
			_ => throw new KernPanic($"спецификатор '{type}' не применим к {v.Kind}"),
		};
	}

	// ------------------------------------------------------------ приведения

	private static ValueKind? NumKind(PrimKind k) => k switch
	{
		PrimKind.I8 => ValueKind.I8, PrimKind.I16 => ValueKind.I16, PrimKind.I32 => ValueKind.I32, PrimKind.I64 => ValueKind.I64,
		PrimKind.U8 => ValueKind.U8, PrimKind.U16 => ValueKind.U16, PrimKind.U32 => ValueKind.U32, PrimKind.U64 => ValueKind.U64,
		PrimKind.F16 => ValueKind.F16, PrimKind.F32 => ValueKind.F32, PrimKind.F64 => ValueKind.F64, _ => null,
	};

	/// <summary>`as`/числовое приведение: числа/char/str/bool — конверсия; иначе проверка типа (для `T?` неудача — nil, иначе panic).</summary>
	internal KValue CastValue(KValue v, KType t)
	{
		if (t is KNullableType n)
		{
			if (v.IsNilOrNone) return KValue.Nil;
			return TryCast(v, n.Inner, out var r) ? r : KValue.Nil;
		}
		if (TryCast(v, t, out var res)) return res;
		throw new KernPanic($"приведение невозможно: значение типа {TypeOf(v).Name} не является {t.Name}");
	}

	private bool TryCast(KValue v, KType t, out KValue result)
	{
		result = v;
		if (t is KPrimType p)
		{
			if (NumKind(p.Kind) is { } nk && (v.IsNumber || v.Kind == ValueKind.Char)) { result = Num.Convert(v, nk); return true; }
			switch (p.Kind)
			{
				case PrimKind.Char when v.IsInteger: result = KValue.Char((char)v.Bits); return true;
				case PrimKind.Str: result = KValue.Str(ValueOps.ToStr(v)); return true;
				case PrimKind.Bool when v.Kind == ValueKind.Bool: return true;
				case PrimKind.Object: return !v.IsNilOrNone;
			}
		}
		return IsInstance(v, t);
	}
}
