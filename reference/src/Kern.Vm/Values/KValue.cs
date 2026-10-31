using System.Runtime.CompilerServices;

namespace Kern.Vm.Values;

public enum ValueKind : byte
{
	None, Nil, Void, Bool, Char,
	I8, I16, I32, I64, U8, U16, U32, U64, F16, F32, F64,
	Str, Ref,
}

/// <summary>Универсальное значение VM. Числа хранятся в Bits (floats — через BitConverter), ссылки в Ref.</summary>
public readonly struct KValue : IEquatable<KValue>
{
	public readonly ValueKind Kind;
	public readonly long Bits;
	public readonly object? Ref;

	public KValue(ValueKind kind, long bits, object? reference = null) { Kind = kind; Bits = bits; Ref = reference; }

	public static readonly KValue None = new(ValueKind.None, 0);
	public static readonly KValue Nil = new(ValueKind.Nil, 0);
	public static readonly KValue Void = new(ValueKind.Void, 0);
	public static readonly KValue True = new(ValueKind.Bool, 1);
	public static readonly KValue False = new(ValueKind.Bool, 0);

	public static KValue Bool(bool v) => v ? True : False;
	public static KValue Char(char c) => new(ValueKind.Char, c);
	public static KValue I8(sbyte v) => new(ValueKind.I8, v);
	public static KValue I16(short v) => new(ValueKind.I16, v);
	public static KValue I32(int v) => new(ValueKind.I32, v);
	public static KValue I64(long v) => new(ValueKind.I64, v);
	public static KValue U8(byte v) => new(ValueKind.U8, v);
	public static KValue U16(ushort v) => new(ValueKind.U16, v);
	public static KValue U32(uint v) => new(ValueKind.U32, v);
	public static KValue U64(ulong v) => new(ValueKind.U64, unchecked((long)v));
	public static KValue F16(Half v) => new(ValueKind.F16, BitConverter.DoubleToInt64Bits((double)v));
	public static KValue F32(float v) => new(ValueKind.F32, BitConverter.DoubleToInt64Bits(v));
	public static KValue F64(double v) => new(ValueKind.F64, BitConverter.DoubleToInt64Bits(v));
	public static KValue Str(string s) => new(ValueKind.Str, 0, s);
	public static KValue Obj(object o) => new(ValueKind.Ref, 0, o);

	public bool IsNil => Kind == ValueKind.Nil;
	public bool IsNone => Kind == ValueKind.None;
	public bool IsNilOrNone => Kind is ValueKind.Nil or ValueKind.None;
	public bool IsInteger => Kind is >= ValueKind.I8 and <= ValueKind.U64;
	public bool IsFloat => Kind is ValueKind.F16 or ValueKind.F32 or ValueKind.F64;
	public bool IsNumber => IsInteger || IsFloat;
	public bool IsUnsigned => Kind is ValueKind.U8 or ValueKind.U16 or ValueKind.U32 or ValueKind.U64;

	public bool AsBool => Bits != 0;
	public char AsChar => (char)Bits;
	public int AsI32 => (int)Bits;
	public long AsI64 => Bits;
	public ulong AsU64 => unchecked((ulong)Bits);
	public double AsF64 => IsFloat ? BitConverter.Int64BitsToDouble(Bits) : IsUnsigned ? AsU64 : Bits;
	public string AsStr => (string)Ref!;
	public T As<T>() where T : class => Unsafe.As<T>(Ref)!;

	public bool Equals(KValue o) => Kind == o.Kind && Bits == o.Bits && Equals(Ref, o.Ref);
	public override bool Equals(object? obj) => obj is KValue v && Equals(v);
	public override int GetHashCode() => HashCode.Combine(Kind, Bits, Ref);
}
