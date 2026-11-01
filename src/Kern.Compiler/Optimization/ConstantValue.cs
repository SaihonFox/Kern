using Kern.Bytecode;

namespace Kern.Compiler.Optimization;

/// <summary>
/// Константа на стеке во время свёртки: вид (тот же, что у <see cref="PrimKind"/>),
/// биты и текст для <c>str</c>. Биты кодируются как в VM: целые — значение в <c>long</c>
/// (знаковый вид расширен, <c>u64</c> — беззнаковые биты), дробные — биты <c>double</c>.
/// </summary>
internal readonly record struct ConstantValue(PrimKind Kind, long Bits, string? Text)
{
	public static ConstantValue Bool(bool value) => new(PrimKind.Bool, value ? 1 : 0, null);

	public static ConstantValue Str(string value) => new(PrimKind.Str, 0, value);

	public static ConstantValue Nil => new(PrimKind.Nil, 0, null);

	public static ConstantValue None => new(PrimKind.None, 0, null);

	public static ConstantValue Void => new(PrimKind.Void, 0, null);

	public static ConstantValue Integer(PrimKind kind, long bits) => new(kind, CanonicalInteger(kind, bits), null);

	public static ConstantValue Float(PrimKind kind, double value) => new(kind, CanonicalFloat(kind, value), null);

	public bool AsBool => Bits != 0;

	public char AsChar => (char)(ushort)Bits;

	public ulong AsU64 => unchecked((ulong)Bits);

	public double AsDouble => Kind switch
	{
		PrimKind.Char => AsChar,
		PrimKind.U8 or PrimKind.U16 or PrimKind.U32 or PrimKind.U64 => AsU64,
		PrimKind.F16 or PrimKind.F32 or PrimKind.F64 => BitConverter.Int64BitsToDouble(Bits),
		_ => Bits,
	};

	public static bool IsInteger(PrimKind kind) =>
		kind is PrimKind.I8 or PrimKind.I16 or PrimKind.I32 or PrimKind.I64
			or PrimKind.U8 or PrimKind.U16 or PrimKind.U32 or PrimKind.U64;

	public static bool IsSigned(PrimKind kind) =>
		kind is PrimKind.I8 or PrimKind.I16 or PrimKind.I32 or PrimKind.I64;

	public static bool IsFloat(PrimKind kind) => kind is PrimKind.F16 or PrimKind.F32 or PrimKind.F64;

	public static bool IsNumber(PrimKind kind) => IsInteger(kind) || IsFloat(kind);

	public static int Width(PrimKind kind) => kind switch
	{
		PrimKind.I8 or PrimKind.U8 => 8,
		PrimKind.I16 or PrimKind.U16 => 16,
		PrimKind.I32 or PrimKind.U32 => 32,
		_ => 64,
	};

	/// <summary>Приведение целых битов к канонической форме вида (как конструкторы <c>KValue</c>).</summary>
	public static long CanonicalInteger(PrimKind kind, long bits) => kind switch
	{
		PrimKind.I8 => unchecked((sbyte)bits),
		PrimKind.I16 => unchecked((short)bits),
		PrimKind.I32 => unchecked((int)bits),
		PrimKind.I64 => bits,
		PrimKind.U8 => unchecked((byte)bits),
		PrimKind.U16 => unchecked((ushort)bits),
		PrimKind.U32 => unchecked((uint)bits),
		PrimKind.U64 => unchecked((long)unchecked((ulong)bits)),
		_ => bits,
	};

	/// <summary>Дробное значение в канонических битах вида: <c>f16</c>/<c>f32</c> округляются.</summary>
	public static long CanonicalFloat(PrimKind kind, double value) => kind switch
	{
		PrimKind.F16 => BitConverter.DoubleToInt64Bits((double)(Half)value),
		PrimKind.F32 => BitConverter.DoubleToInt64Bits((double)(float)value),
		_ => BitConverter.DoubleToInt64Bits(value),
	};

	/// <summary>Сколько слотов кода займёт каноническая запись константы.</summary>
	public static int EstimatedSize(ConstantValue value) => value.Kind switch
	{
		PrimKind.Bool or PrimKind.Nil or PrimKind.None or PrimKind.Void => 1,
		PrimKind.Char or PrimKind.Str or PrimKind.I32 => 2,
		PrimKind.I64 or PrimKind.F64 => 3,
		PrimKind.U64 => 4,
		PrimKind.I8 or PrimKind.I16 or PrimKind.U8 or PrimKind.U16 or PrimKind.U32 => 3,
		PrimKind.F16 or PrimKind.F32 => 4,
		_ => 3,
	};

	/// <summary>
	/// Записать константу каноническими инструкциями: та же пара «значение + вид», что
	/// получилась бы у литерала в этом контексте. Возвращает число записанных слотов.
	/// </summary>
	public int Emit(List<int> code, KbcModule module)
	{
		switch (Kind)
		{
			case PrimKind.Bool:
				code.Add((int)(AsBool ? OpCode.PushTrue : OpCode.PushFalse));
				return 1;
			case PrimKind.Nil:
				code.Add((int)OpCode.PushNil);
				return 1;
			case PrimKind.None:
				code.Add((int)OpCode.PushNone);
				return 1;
			case PrimKind.Void:
				code.Add((int)OpCode.PushVoid);
				return 1;
			case PrimKind.Char:
				code.Add((int)OpCode.PushConst);
				code.Add(module.InternConst(KConst.Char(AsChar)));
				return 2;
			case PrimKind.Str:
				code.Add((int)OpCode.PushConst);
				code.Add(module.InternConst(KConst.Str(Text ?? string.Empty)));
				return 2;
			case PrimKind.I32:
				code.Add((int)OpCode.PushI32);
				code.Add(unchecked((int)Bits));
				return 2;
			case PrimKind.I64:
				code.Add((int)OpCode.PushI64);
				code.Add(unchecked((int)Bits));
				code.Add(unchecked((int)(Bits >> 32)));
				return 3;
			case PrimKind.F64:
				code.Add((int)OpCode.PushF64);
				code.Add(unchecked((int)Bits));
				code.Add(unchecked((int)(Bits >> 32)));
				return 3;
			case PrimKind.I8:
				code.Add((int)OpCode.PushI32);
				code.Add(unchecked((int)(sbyte)Bits));
				code.Add((int)OpCode.ConvI8);
				return 3;
			case PrimKind.I16:
				code.Add((int)OpCode.PushI32);
				code.Add(unchecked((int)(short)Bits));
				code.Add((int)OpCode.ConvI16);
				return 3;
			case PrimKind.U8:
				code.Add((int)OpCode.PushI32);
				code.Add(unchecked((byte)Bits));
				code.Add((int)OpCode.ConvU8);
				return 3;
			case PrimKind.U16:
				code.Add((int)OpCode.PushI32);
				code.Add(unchecked((ushort)Bits));
				code.Add((int)OpCode.ConvU16);
				return 3;
			case PrimKind.U32:
				code.Add((int)OpCode.PushI32);
				code.Add(unchecked((int)(uint)Bits));
				code.Add((int)OpCode.ConvU32);
				return 3;
			case PrimKind.U64:
				code.Add((int)OpCode.PushI64);
				code.Add(unchecked((int)Bits));
				code.Add(unchecked((int)(Bits >> 32)));
				code.Add((int)OpCode.ConvU64);
				return 4;
			case PrimKind.F16:
				code.Add((int)OpCode.PushF64);
				code.Add(unchecked((int)Bits));
				code.Add(unchecked((int)(Bits >> 32)));
				code.Add((int)OpCode.ConvF16);
				return 4;
			case PrimKind.F32:
				code.Add((int)OpCode.PushF64);
				code.Add(unchecked((int)Bits));
				code.Add(unchecked((int)(Bits >> 32)));
				code.Add((int)OpCode.ConvF32);
				return 4;
			default:
				code.Add((int)OpCode.PushNone);
				return 1;
		}
	}

	/// <summary>Целевой вид опкода преобразования; false — опкод не является <c>Conv*</c>.</summary>
	public static bool TryConversionTarget(OpCode op, out PrimKind target)
	{
		target = op switch
		{
			OpCode.ConvI8 => PrimKind.I8,
			OpCode.ConvI16 => PrimKind.I16,
			OpCode.ConvI32 => PrimKind.I32,
			OpCode.ConvI64 => PrimKind.I64,
			OpCode.ConvU8 => PrimKind.U8,
			OpCode.ConvU16 => PrimKind.U16,
			OpCode.ConvU32 => PrimKind.U32,
			OpCode.ConvU64 => PrimKind.U64,
			OpCode.ConvF16 => PrimKind.F16,
			OpCode.ConvF32 => PrimKind.F32,
			OpCode.ConvF64 => PrimKind.F64,
			OpCode.ConvChar => PrimKind.Char,
			OpCode.ConvBool => PrimKind.Bool,
			_ => PrimKind.Object,
		};
		return target != PrimKind.Object;
	}

	/// <summary>
	/// Безопасное приведение константы: повторяет <c>KValueMath.Convert</c>. Если VM на таком
	/// приведении паникует, свёртка не выполняется (false).
	/// </summary>
	public static bool TryConvert(ConstantValue value, PrimKind target, out ConstantValue result)
	{
		if (value.Kind == target)
		{
			result = value;
			return true;
		}

		if (target == PrimKind.Bool)
		{
			if (IsNumber(value.Kind))
			{
				result = Bool(value.Bits != 0);
				return true;
			}

			result = default;
			return false;
		}

		if (target == PrimKind.Char)
		{
			if (IsNumber(value.Kind) || value.Kind == PrimKind.Char)
			{
				result = Integer(PrimKind.Char, (ushort)ToInt64(value));
				return true;
			}

			result = default;
			return false;
		}

		if (!IsNumber(target) && target != PrimKind.Char)
		{
			result = default;
			return false;
		}

		if (!IsNumber(value.Kind) && value.Kind != PrimKind.Char)
		{
			result = default;
			return false;
		}

		result = IsFloat(target)
			? Float(target, ToDouble(value))
			: Integer(target, ToInt64(value));
		return true;
	}

	/// <summary>Как <c>KValueMath</c>: дробное в целое — C#-приведение, беззнаковое — по битам.</summary>
	public static long ToInt64(ConstantValue value) => value.Kind switch
	{
		PrimKind.Char => value.AsChar,
		PrimKind.I8 or PrimKind.I16 or PrimKind.I32 or PrimKind.I64 => value.Bits,
		PrimKind.U8 or PrimKind.U16 or PrimKind.U32 or PrimKind.U64 => unchecked((long)value.AsU64),
		PrimKind.F16 or PrimKind.F32 or PrimKind.F64 => (long)BitConverter.Int64BitsToDouble(value.Bits),
		_ => 0,
	};

	public static double ToDouble(ConstantValue value) => value.Kind == PrimKind.Char
		? value.AsChar
		: value.AsDouble;

	/// <summary>Сравнение чисел как в <c>KValueMath.CompareNumbers</c>; результат -1/0/1.</summary>
	public static int CompareNumbers(ConstantValue left, ConstantValue right)
	{
		if (IsFloat(left.Kind) || IsFloat(right.Kind))
			return left.AsDouble.CompareTo(right.AsDouble);
		return CompareIntegers(left, right);
	}

	public static int CompareIntegers(ConstantValue left, ConstantValue right)
	{
		if (left.Kind == right.Kind)
			return left.Bits.CompareTo(right.Bits);
		if (!IsSigned(left.Kind) && !IsSigned(right.Kind))
			return left.AsU64.CompareTo(right.AsU64);
		if (IsSigned(left.Kind) && IsSigned(right.Kind))
			return left.Bits.CompareTo(right.Bits);
		if (IsSigned(left.Kind))
			return left.Bits < 0 ? -1 : unchecked((ulong)left.Bits).CompareTo(right.AsU64);
		return right.Bits < 0 ? 1 : left.AsU64.CompareTo(unchecked((ulong)right.Bits));
	}

	/// <summary>Равенство примитивов как в <c>KValueMath.AreEqual</c> (без ссылок).</summary>
	public static bool ValuesEqual(ConstantValue left, ConstantValue right)
	{
		if (IsNumber(left.Kind) && IsNumber(right.Kind))
			return CompareNumbers(left, right) == 0;
		if (left.Kind != right.Kind)
			return false;
		return left.Kind switch
		{
			PrimKind.None or PrimKind.Nil or PrimKind.Void => true,
			PrimKind.Bool or PrimKind.Char => left.Bits == right.Bits,
			PrimKind.Str => string.Equals(left.Text, right.Text, StringComparison.Ordinal),
			_ => left.Bits == right.Bits,
		};
	}
}
