using System.Globalization;

namespace Kern.Vm.Values;

/// <summary>
/// Точный вид значения VM. Числовые виды идут подряд (I8..U64) — это используется
/// в проверках <see cref="KValue.IsInteger"/>.
/// </summary>
public enum ValueKind : byte
{
	/// <summary>Отсутствующее значение (например, незаполненный локальный слот).</summary>
	None = 0,
	Nil,
	Void,
	Bool,
	Char,
	I8,
	I16,
	I32,
	I64,
	U8,
	U16,
	U32,
	U64,
	F16,
	F32,
	F64,
	Str,
	/// <summary>Ссылка на объект (unit/type/list/dict/tuple/task/…); сам объект — в <see cref="KValue.Ref"/>.</summary>
	Ref,
}

/// <summary>
/// Универсальное значение VM: вид + 64 бита + необязательная ссылка.
/// Числа хранятся в <see cref="Bits"/> (f16/f32/f64 — как double-биты для единообразия),
/// строки и объекты — в <see cref="Ref"/>. Структура, поэтому копируется по значению.
/// </summary>
public readonly struct KValue : IEquatable<KValue>
{
	public readonly ValueKind Kind;
	public readonly long Bits;
	public readonly object? Ref;

	public KValue(ValueKind kind, long bits, object? reference = null)
	{
		Kind = kind;
		Bits = bits;
		Ref = reference;
	}

	public static readonly KValue None = new(ValueKind.None, 0);
	public static readonly KValue Nil = new(ValueKind.Nil, 0);
	public static readonly KValue Void = new(ValueKind.Void, 0);
	public static readonly KValue True = new(ValueKind.Bool, 1);
	public static readonly KValue False = new(ValueKind.Bool, 0);

	public static KValue Bool(bool value) => value ? True : False;
	public static KValue Char(char value) => new(ValueKind.Char, value);
	public static KValue I8(sbyte value) => new(ValueKind.I8, value);
	public static KValue I16(short value) => new(ValueKind.I16, value);
	public static KValue I32(int value) => new(ValueKind.I32, value);
	public static KValue I64(long value) => new(ValueKind.I64, value);
	public static KValue U8(byte value) => new(ValueKind.U8, value);
	public static KValue U16(ushort value) => new(ValueKind.U16, value);
	public static KValue U32(uint value) => new(ValueKind.U32, value);
	public static KValue U64(ulong value) => new(ValueKind.U64, unchecked((long)value));
	public static KValue F16(Half value) => new(ValueKind.F16, BitConverter.DoubleToInt64Bits((double)value));
	public static KValue F32(float value) => new(ValueKind.F32, BitConverter.DoubleToInt64Bits(value));
	public static KValue F64(double value) => new(ValueKind.F64, BitConverter.DoubleToInt64Bits(value));
	public static KValue Str(string value) => new(ValueKind.Str, 0, value ?? throw new ArgumentNullException(nameof(value)));
	public static KValue Obj(object value) => new(ValueKind.Ref, 0, value ?? throw new ArgumentNullException(nameof(value)));

	public bool IsNil => Kind == ValueKind.Nil;
	public bool IsNone => Kind == ValueKind.None;
	public bool IsVoid => Kind == ValueKind.Void;
	public bool IsNilOrNone => Kind is ValueKind.Nil or ValueKind.None;

	public bool IsInteger => (byte)Kind >= (byte)ValueKind.I8 && (byte)Kind <= (byte)ValueKind.U64;
	public bool IsSignedInteger => (byte)Kind >= (byte)ValueKind.I8 && (byte)Kind <= (byte)ValueKind.I64;
	public bool IsUnsignedInteger => (byte)Kind >= (byte)ValueKind.U8 && (byte)Kind <= (byte)ValueKind.U64;
	public bool IsFloat => Kind is ValueKind.F16 or ValueKind.F32 or ValueKind.F64;
	public bool IsNumber => IsInteger || IsFloat;

	public bool AsBool => Bits != 0;
	public char AsChar => (char)(ushort)Bits;
	public sbyte AsI8 => unchecked((sbyte)Bits);
	public short AsI16 => unchecked((short)Bits);
	public int AsI32 => unchecked((int)Bits);
	public long AsI64 => Bits;
	public byte AsU8 => unchecked((byte)Bits);
	public ushort AsU16 => unchecked((ushort)Bits);
	public uint AsU32 => unchecked((uint)Bits);
	public ulong AsU64 => unchecked((ulong)Bits);
	public Half AsF16 => (Half)AsF64;
	public float AsF32 => (float)AsF64;
	public double AsF64 => Kind switch
	{
		ValueKind.F16 or ValueKind.F32 or ValueKind.F64 => BitConverter.Int64BitsToDouble(Bits),
		ValueKind.U8 or ValueKind.U16 or ValueKind.U32 or ValueKind.U64 => AsU64,
		_ => Bits,
	};
	public string AsStr => (string)Ref!;
	public T AsObject<T>() where T : class => (T)Ref!;

	/// <summary>Kern-имя вида значения — для сообщений panic.</summary>
	public string TypeName => Kind switch
	{
		ValueKind.None => "none",
		ValueKind.Nil => "nil",
		ValueKind.Void => "void",
		ValueKind.Bool => "bool",
		ValueKind.Char => "char",
		ValueKind.I8 => "i8",
		ValueKind.I16 => "i16",
		ValueKind.I32 => "i32",
		ValueKind.I64 => "i64",
		ValueKind.U8 => "u8",
		ValueKind.U16 => "u16",
		ValueKind.U32 => "u32",
		ValueKind.U64 => "u64",
		ValueKind.F16 => "f16",
		ValueKind.F32 => "f32",
		ValueKind.F64 => "f64",
		ValueKind.Str => "str",
		_ => "object",
	};

	/// <summary>
	/// Равенство значений: unit — структурное (тип и поля, spec §9), остальное — по
	/// прежнему правилу; подробности в <see cref="KValueMath.StructuralEquals"/>.
	/// </summary>
	public bool Equals(KValue other) => KValueMath.StructuralEquals(this, other);

	public override bool Equals(object? obj) => obj is KValue other && Equals(other);

	/// <summary>Хэш, согласованный с <see cref="Equals(KValue)"/> (см. <see cref="KValueMath.StructuralHash"/>).</summary>
	public override int GetHashCode() => KValueMath.StructuralHash(this);

	public override string ToString() => Kind switch
	{
		ValueKind.None => "none",
		ValueKind.Nil => "nil",
		ValueKind.Void => "void",
		ValueKind.Bool => AsBool ? "true" : "false",
		ValueKind.Char => AsChar.ToString(),
		ValueKind.Str => AsStr,
		ValueKind.Ref => Ref?.ToString() ?? "null",
		ValueKind.F16 or ValueKind.F32 or ValueKind.F64 => AsF64.ToString("R", CultureInfo.InvariantCulture),
		ValueKind.U8 or ValueKind.U16 or ValueKind.U32 or ValueKind.U64 => AsU64.ToString(CultureInfo.InvariantCulture),
		_ => Bits.ToString(CultureInfo.InvariantCulture),
	};
}
