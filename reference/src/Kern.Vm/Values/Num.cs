using System.Globalization;
using Kern.Vm.Execution;

namespace Kern.Vm.Values;

/// <summary>Точные числовые типы: wrap целых, конверсии, повышение внутри семейства.</summary>
public static class Num
{
	public static bool IsUnsigned(ValueKind k) => k is ValueKind.U8 or ValueKind.U16 or ValueKind.U32 or ValueKind.U64;
	public static bool IsFloat(ValueKind k) => k is ValueKind.F16 or ValueKind.F32 or ValueKind.F64;
	public static bool IsInt(ValueKind k) => k is >= ValueKind.I8 and <= ValueKind.U64;

	public static int BitsOf(ValueKind k) => k switch
	{
		ValueKind.I8 or ValueKind.U8 => 8,
		ValueKind.I16 or ValueKind.U16 or ValueKind.F16 => 16,
		ValueKind.I32 or ValueKind.U32 or ValueKind.F32 => 32,
		_ => 64,
	};

	/// <summary>Обрезать значение до разрядности типа (wrap, как unchecked в C#).</summary>
	public static KValue Wrap(ValueKind k, long v) => k switch
	{
		ValueKind.I8 => new(k, (sbyte)v),
		ValueKind.I16 => new(k, (short)v),
		ValueKind.I32 => new(k, (int)v),
		ValueKind.U8 => new(k, (byte)v),
		ValueKind.U16 => new(k, (ushort)v),
		ValueKind.U32 => new(k, (uint)v),
		_ => new(k, v),
	};

	public static KValue FromDouble(ValueKind k, double d) => k switch
	{
		ValueKind.F16 => KValue.F16((Half)d),
		ValueKind.F32 => KValue.F32((float)d),
		_ => KValue.F64(d),
	};

	/// <summary>Результирующий целый тип бинарной операции (повышение внутри семейства).</summary>
	public static ValueKind ResultInt(ValueKind a, ValueKind b)
	{
		if (a == b) return a;
		bool ua = IsUnsigned(a), ub = IsUnsigned(b);
		int ba = BitsOf(a), bb = BitsOf(b);
		if (ua == ub) return ba >= bb ? a : b;
		int sBits = ua ? bb : ba, uBits = ua ? ba : bb;
		int bits = Math.Max(sBits, Math.Min(64, uBits * 2));
		return bits switch { 8 => ValueKind.I8, 16 => ValueKind.I16, 32 => ValueKind.I32, _ => ValueKind.I64 };
	}

	public static ValueKind ResultFloat(KValue a, KValue b)
	{
		if (a.IsFloat && b.IsFloat) return BitsOf(a.Kind) >= BitsOf(b.Kind) ? a.Kind : b.Kind;
		return ValueKind.F64; // смешивание int/float терпим, результат f64
	}

	/// <summary>Сравнение чисел: -1/0/1, либо 2 если не упорядочены (NaN).</summary>
	public static int Compare(KValue a, KValue b)
	{
		if (a.IsFloat || b.IsFloat)
		{
			double x = a.AsF64, y = b.AsF64;
			if (double.IsNaN(x) || double.IsNaN(y)) return 2;
			return x < y ? -1 : x > y ? 1 : 0;
		}
		bool ua = a.IsUnsigned, ub = b.IsUnsigned;
		if (ua == ub) return ua ? a.AsU64.CompareTo(b.AsU64) : a.Bits.CompareTo(b.Bits);
		// смешанные знаковость
		if (ua) return b.Bits < 0 ? 1 : a.AsU64.CompareTo((ulong)b.Bits);
		return a.Bits < 0 ? -1 : ((ulong)a.Bits).CompareTo(b.AsU64);
	}

	/// <summary>Явная конверсия в числовой тип (as): wrap для целых, усечение для float→int.</summary>
	public static KValue Convert(KValue v, ValueKind target)
	{
		if (v.Kind == ValueKind.Char) v = KValue.I32(v.AsChar);
		if (!v.IsNumber) throw new KernPanic($"нельзя привести {v.Kind} к числовому типу");
		if (IsFloat(target)) return FromDouble(target, v.AsF64);
		if (v.IsFloat)
		{
			double d = v.AsF64;
			if (double.IsNaN(d)) return Wrap(target, 0);
			long l = d >= 9.2233720368547758E18 ? long.MaxValue : d <= -9.2233720368547758E18 ? long.MinValue : (long)d;
			if (target == ValueKind.U64 && d >= 9.2233720368547758E18) l = unchecked((long)(ulong)Math.Min(d, 1.8446744073709552E19));
			return Wrap(target, l);
		}
		return Wrap(target, v.Bits);
	}

	public static string ToStr(KValue v) => v.Kind switch
	{
		ValueKind.F16 => ((Half)v.AsF64).ToString(null, CultureInfo.InvariantCulture),
		ValueKind.F32 => ((float)v.AsF64).ToString(CultureInfo.InvariantCulture),
		ValueKind.F64 => FormatDouble(v.AsF64),
		ValueKind.U64 => v.AsU64.ToString(CultureInfo.InvariantCulture),
		_ => v.Bits.ToString(CultureInfo.InvariantCulture),
	};

	private static string FormatDouble(double d) => d.ToString("R", CultureInfo.InvariantCulture);

	/// <summary>KValue из CLR-значения (для нативов/dotnet-интеропа).</summary>
	public static KValue FromClr(object? o) => o switch
	{
		null => KValue.Nil,
		KValue kv => kv,
		bool b => KValue.Bool(b),
		char c => KValue.Char(c),
		sbyte x => KValue.I8(x),
		short x => KValue.I16(x),
		int x => KValue.I32(x),
		long x => KValue.I64(x),
		byte x => KValue.U8(x),
		ushort x => KValue.U16(x),
		uint x => KValue.U32(x),
		ulong x => KValue.U64(x),
		Half x => KValue.F16(x),
		float x => KValue.F32(x),
		double x => KValue.F64(x),
		string s => KValue.Str(s),
		_ => KValue.Obj(o is KNativeBox || IsVmObject(o) ? o : new KNativeBox(o)),
	};

	private static bool IsVmObject(object o) => o.GetType().Namespace?.StartsWith("Kern.Vm", StringComparison.Ordinal) == true;

	/// <summary>KValue → CLR-значение (числа и строки — примитивами, KNativeBox — внутренним значением).</summary>
	public static object? ToClr(KValue v) => v.Kind switch
	{
		ValueKind.None or ValueKind.Nil or ValueKind.Void => null,
		ValueKind.Bool => v.AsBool,
		ValueKind.Char => v.AsChar,
		ValueKind.I8 => (sbyte)v.Bits,
		ValueKind.I16 => (short)v.Bits,
		ValueKind.I32 => (int)v.Bits,
		ValueKind.I64 => v.Bits,
		ValueKind.U8 => (byte)v.Bits,
		ValueKind.U16 => (ushort)v.Bits,
		ValueKind.U32 => (uint)v.Bits,
		ValueKind.U64 => v.AsU64,
		ValueKind.F16 => (Half)v.AsF64,
		ValueKind.F32 => (float)v.AsF64,
		ValueKind.F64 => v.AsF64,
		ValueKind.Str => v.Ref,
		_ => v.Ref is KNativeBox nb ? nb.Value : v.Ref,
	};
}
