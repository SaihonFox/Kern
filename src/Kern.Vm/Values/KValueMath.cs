using System.Runtime.CompilerServices;
using Kern.Vm.Execution;
using Kern.Vm.Types;

namespace Kern.Vm.Values;

/// <summary>
/// Точная числовая арифметика VM: операнды обязаны иметь один и тот же числовой вид
/// (приведение делает компилятор через <c>Conv*</c>). Переполнение целых — wrap, как в C#
/// (unchecked); деление/остаток целых на ноль — panic; у дробных — IEEE-семантика.
/// </summary>
public static class KValueMath
{
	// ---------- арифметика ----------

	public static KValue Add(KValue a, KValue b) => Arithmetic(a, b, ArithOp.Add);

	public static KValue Sub(KValue a, KValue b) => Arithmetic(a, b, ArithOp.Sub);

	public static KValue Mul(KValue a, KValue b) => Arithmetic(a, b, ArithOp.Mul);

	public static KValue Div(KValue a, KValue b) => Arithmetic(a, b, ArithOp.Div);

	public static KValue Mod(KValue a, KValue b) => Arithmetic(a, b, ArithOp.Mod);

	public static KValue Pow(KValue a, KValue b) => Arithmetic(a, b, ArithOp.Pow);

	public static KValue Neg(KValue value) => value.Kind switch
	{
		ValueKind.I8 => KValue.I8(unchecked((sbyte)(-value.Bits))),
		ValueKind.I16 => KValue.I16(unchecked((short)(-value.Bits))),
		ValueKind.I32 => KValue.I32(unchecked((int)(-value.Bits))),
		ValueKind.I64 => KValue.I64(unchecked(-value.Bits)),
		ValueKind.F16 => KValue.F16((Half)(-value.AsF64)),
		ValueKind.F32 => KValue.F32(-value.AsF32),
		ValueKind.F64 => KValue.F64(-value.AsF64),
		_ => throw new KernPanic($"унарный минус не поддерживается для {value.TypeName}"),
	};

	// ---------- битовые операции ----------

	public static KValue BitAnd(KValue a, KValue b) => Bitwise(a, b, BitwiseOp.And);

	public static KValue BitOr(KValue a, KValue b) => Bitwise(a, b, BitwiseOp.Or);

	public static KValue BitXor(KValue a, KValue b) => Bitwise(a, b, BitwiseOp.Xor);

	public static KValue Shl(KValue a, KValue b) => Bitwise(a, b, BitwiseOp.Shl);

	public static KValue Shr(KValue a, KValue b) => Bitwise(a, b, BitwiseOp.Shr);

	public static KValue BitNot(KValue value) =>
		value.IsInteger
			? WrapInteger(value.Kind, ~(ulong)value.Bits)
			: throw new KernPanic($"битовое отрицание не поддерживается для {value.TypeName}");

	private enum BitwiseOp { And, Or, Xor, Shl, Shr }

	private static string BitwiseName(BitwiseOp op) => op switch
	{
		BitwiseOp.And => "&",
		BitwiseOp.Or => "|",
		BitwiseOp.Xor => "^",
		BitwiseOp.Shl => "<<",
		_ => ">>",
	};

	/// <summary>
	/// Битовая операция над целыми. Для `&amp;`, `|`, `^` виды операндов обязаны совпадать;
	/// для сдвигов вид берётся с левого операнда, а счётчик может быть любым целым
	/// (binder намеренно его не приводит). Счётчик маскируется как в C#
	/// (31 для 8/16/32-битных, 63 для 64-битных), поэтому сдвиг не паникует.
	/// `&gt;&gt;` для знаковых — арифметический (знак сохраняется), для беззнаковых — логический.
	/// </summary>
	private static KValue Bitwise(KValue a, KValue b, BitwiseOp op)
	{
		if (!a.IsInteger || !b.IsInteger)
			throw new KernPanic($"битовая операция '{BitwiseName(op)}' требует целых операндов: {a.TypeName} и {b.TypeName}");
		if (op is not (BitwiseOp.Shl or BitwiseOp.Shr) && a.Kind != b.Kind)
			throw new KernPanic($"несовпадение типов операндов {BitwiseName(op)}: {a.TypeName} и {b.TypeName}");

		var mask = (ulong)(IntegerBits(a.Kind) == 64 ? 63 : 31);
		var count = (int)((ulong)b.Bits & mask);
		var result = op switch
		{
			BitwiseOp.And => (ulong)a.Bits & (ulong)b.Bits,
			BitwiseOp.Or => (ulong)a.Bits | (ulong)b.Bits,
			BitwiseOp.Xor => (ulong)a.Bits ^ (ulong)b.Bits,
			BitwiseOp.Shl => (ulong)a.Bits << count,
			_ => a.IsSignedInteger ? (ulong)(a.Bits >> count) : (ulong)a.Bits >> count,
		};
		return WrapInteger(a.Kind, result);
	}

	private static int IntegerBits(ValueKind kind) => kind switch
	{
		ValueKind.I8 or ValueKind.U8 => 8,
		ValueKind.I16 or ValueKind.U16 => 16,
		ValueKind.I32 or ValueKind.U32 => 32,
		_ => 64,
	};

	private static KValue WrapInteger(ValueKind kind, ulong bits) => kind switch
	{
		ValueKind.I8 => KValue.I8(unchecked((sbyte)bits)),
		ValueKind.I16 => KValue.I16(unchecked((short)bits)),
		ValueKind.I32 => KValue.I32(unchecked((int)bits)),
		ValueKind.I64 => KValue.I64(unchecked((long)bits)),
		ValueKind.U8 => KValue.U8(unchecked((byte)bits)),
		ValueKind.U16 => KValue.U16(unchecked((ushort)bits)),
		ValueKind.U32 => KValue.U32(unchecked((uint)bits)),
		ValueKind.U64 => KValue.U64(bits),
		_ => throw new KernPanic($"битовая операция не поддерживается для {kind}"),
	};

	private enum ArithOp { Add, Sub, Mul, Div, Mod, Pow }

	private static string OpName(ArithOp op) => op switch
	{
		ArithOp.Add => "+",
		ArithOp.Sub => "-",
		ArithOp.Mul => "*",
		ArithOp.Div => "/",
		ArithOp.Mod => "%",
		_ => "**",
	};

	private static KValue Arithmetic(KValue a, KValue b, ArithOp op)
	{
		if (a.Kind == ValueKind.Str && b.Kind == ValueKind.Str)
		{
			if (op != ArithOp.Add)
				throw new KernPanic($"арифметика {OpName(op)} не поддерживается для str");
			return KValue.Str(a.AsStr + b.AsStr);
		}

		if (a.Kind != b.Kind)
			throw new KernPanic($"несовпадение типов операндов {OpName(op)}: {a.TypeName} и {b.TypeName}");

		return a.Kind switch
		{
			ValueKind.I8 => KValue.I8(unchecked((sbyte)SignedResult(a.Bits, b.Bits, op))),
			ValueKind.I16 => KValue.I16(unchecked((short)SignedResult(a.Bits, b.Bits, op))),
			ValueKind.I32 => KValue.I32(unchecked((int)SignedResult(a.Bits, b.Bits, op))),
			ValueKind.I64 => KValue.I64(SignedResult(a.Bits, b.Bits, op)),
			ValueKind.U8 => KValue.U8(unchecked((byte)UnsignedResult(a.AsU64, b.AsU64, op))),
			ValueKind.U16 => KValue.U16(unchecked((ushort)UnsignedResult(a.AsU64, b.AsU64, op))),
			ValueKind.U32 => KValue.U32(unchecked((uint)UnsignedResult(a.AsU64, b.AsU64, op))),
			ValueKind.U64 => KValue.U64(UnsignedResult(a.AsU64, b.AsU64, op)),
			ValueKind.F16 => KValue.F16((Half)FloatResult(a.AsF64, b.AsF64, op)),
			ValueKind.F32 => KValue.F32((float)FloatResult(a.AsF64, b.AsF64, op)),
			ValueKind.F64 => KValue.F64(FloatResult(a.AsF64, b.AsF64, op)),
			_ => throw new KernPanic($"арифметика {OpName(op)} не поддерживается для {a.TypeName}"),
		};
	}

	private static long SignedResult(long a, long b, ArithOp op)
	{
		switch (op)
		{
			case ArithOp.Add: return unchecked(a + b);
			case ArithOp.Sub: return unchecked(a - b);
			case ArithOp.Mul: return unchecked(a * b);
			case ArithOp.Div:
				if (b == 0) throw new KernPanic("деление на ноль");
				// long.MinValue / -1 переполняет long; по контракту wrap, а не OverflowException.
				return a == long.MinValue && b == -1 ? long.MinValue : a / b;
			case ArithOp.Mod:
				if (b == 0) throw new KernPanic("остаток от деления на ноль");
				return a == long.MinValue && b == -1 ? 0 : a % b;
			default:
				return IntegerPow(a, b);
		}
	}

	private static ulong UnsignedResult(ulong a, ulong b, ArithOp op) => op switch
	{
		ArithOp.Add => unchecked(a + b),
		ArithOp.Sub => unchecked(a - b),
		ArithOp.Mul => unchecked(a * b),
		ArithOp.Div => b == 0 ? throw new KernPanic("деление на ноль") : a / b,
		ArithOp.Mod => b == 0 ? throw new KernPanic("остаток от деления на ноль") : a % b,
		_ => UnsignedPow(a, b),
	};

	private static double FloatResult(double a, double b, ArithOp op) => op switch
	{
		ArithOp.Add => a + b,
		ArithOp.Sub => a - b,
		ArithOp.Mul => a * b,
		ArithOp.Div => a / b,
		ArithOp.Mod => a % b,
		_ => Math.Pow(a, b),
	};

	private static long IntegerPow(long value, long exponent)
	{
		if (exponent < 0)
			throw new KernPanic("отрицательная степень для целого числа не поддерживается");
		long result = 1;
		long factor = value;
		ulong power = unchecked((ulong)exponent);
		while (power != 0)
		{
			if ((power & 1) != 0) result = unchecked(result * factor);
			power >>= 1;
			if (power != 0) factor = unchecked(factor * factor);
		}
		return result;
	}

	private static ulong UnsignedPow(ulong value, ulong exponent)
	{
		ulong result = 1;
		ulong factor = value;
		while (exponent != 0)
		{
			if ((exponent & 1) != 0) result = unchecked(result * factor);
			exponent >>= 1;
			if (exponent != 0) factor = unchecked(factor * factor);
		}
		return result;
	}

	// ---------- сравнение ----------

	/// <summary>
	/// Структурное равенство значений VM: unit-объекты — по reified-типу и полям (spec §9),
	/// примитивы — по точному виду и битам (без межвидового числового равенства), строки —
	/// ординально, все прочие ссылки (type/list/dict/…) — по ссылке.
	/// </summary>
	public static bool StructuralEquals(KValue a, KValue b)
	{
		if (a.Kind == ValueKind.Ref && b.Kind == ValueKind.Ref)
		{
			if (a.Ref is KObject left && b.Ref is KObject right && left.IsUnit && right.IsUnit)
				return UnitsEqual(left, right);
			if (a.Ref is KTuple tupleA && b.Ref is KTuple tupleB)
				return TuplesStructuralEquals(tupleA, tupleB);
			return ReferenceEquals(a.Ref, b.Ref);
		}

		if (a.Kind != b.Kind) return false;
		return a.Kind == ValueKind.Str
			? string.Equals(a.AsStr, b.AsStr, StringComparison.Ordinal)
			: a.Bits == b.Bits;
	}

	/// <summary>
	/// Хэш, согласованный с <see cref="StructuralEquals"/>: у unit — тип и поля,
	/// у остальных значений — прежнее поведение <see cref="KValue.GetHashCode"/>.
	/// </summary>
	public static int StructuralHash(KValue value)
	{
		if (value.Kind == ValueKind.Ref && value.Ref is KObject obj && obj.IsUnit)
		{
			HashCode hash = new();
			hash.Add(obj.Type.Canonical, StringComparer.Ordinal);
			hash.Add(obj.FieldCount);
			for (int i = 0; i < obj.FieldCount; i++)
				hash.Add(StructuralHash(obj.GetField(i)));
			return hash.ToHashCode();
		}

		if (value.Kind == ValueKind.Ref && value.Ref is KTuple tuple)
		{
			HashCode hash = new();
			hash.Add(tuple.Count);
			for (int i = 0; i < tuple.Count; i++)
				hash.Add(StructuralHash(tuple[i]));
			return hash.ToHashCode();
		}

		return HashCode.Combine(value.Kind, value.Bits, value.Ref);
	}

	/// <summary>
	/// 64-битный структурный хэш для протокола <c>hash</c> (spec §9/§16). Правило
	/// согласованности то же, что у <see cref="StructuralHash"/>: у unit — reified-тип,
	/// число полей и рекурсивные хэши полей; прочие ссылки — идентичность; примитивы —
	/// точный вид и биты (числа — как хранятся), строки — ординально.
	/// Прежний <see cref="StructuralHash"/> не меняется.
	/// </summary>
	public static ulong StructuralHash64(KValue value)
	{
		if (value.Kind == ValueKind.Ref && value.Ref is KObject obj && obj.IsUnit)
			return ObjectFieldsHash64(obj);

		if (value.Kind == ValueKind.Ref && value.Ref is KTuple tuple)
			return TupleFieldsHash64(tuple);

		if (value.Kind == ValueKind.Ref)
			return unchecked((ulong)RuntimeHelpers.GetHashCode(value.Ref));

		return value.Kind == ValueKind.Str
			? Mix(MixString(HashSeed, value.AsStr), (ulong)ValueKind.Str)
			: Mix((ulong)value.Kind, unchecked((ulong)value.Bits));
	}

	/// <summary>
	/// 64-битный «полевой» хэш для производного <c>hash</c> у <c>type</c> (B8a): в отличие
	/// от <see cref="StructuralHash64"/> объект любого вида (<c>unit</c> и <c>type</c>)
	/// хэшируется по reified-типу, числу и значениям полей. Поля считаются через
	/// <see cref="StructuralHash64"/>, поэтому вложенные unit — структурно, а вложенные
	/// class-ссылки — по идентичности. Для не-объектов результат совпадает с
	/// <see cref="StructuralHash64"/>.
	/// </summary>
	public static ulong FieldHash64(KValue value) =>
		value.Kind == ValueKind.Ref && value.Ref is KObject obj
			? ObjectFieldsHash64(obj)
			: StructuralHash64(value);

	/// <summary>Общая полевая часть хэша: reified-тип, число полей и хэши полей.</summary>
	private static ulong ObjectFieldsHash64(KObject obj)
	{
		ulong hash = MixString(HashSeed, obj.Type.Canonical);
		hash = Mix(hash, (ulong)obj.FieldCount);
		for (int i = 0; i < obj.FieldCount; i++)
			hash = Mix(hash, StructuralHash64(obj.GetField(i)));
		return hash;
	}

	private const ulong HashSeed = 14695981039346656037UL;
	private const ulong HashPrime = 1099511628211UL;

	/// <summary>FNV-1a по UTF-16 code units: детерминированно (в отличие от string.GetHashCode).</summary>
	private static ulong MixString(ulong state, string text)
	{
		ulong hash = state;
		foreach (char c in text)
		{
			hash ^= c;
			hash = unchecked(hash * HashPrime);
		}
		return hash;
	}

	/// <summary>Перемешивание пары (состояние, значение) в 64 бита (финализатор splitmix64).</summary>
	private static ulong Mix(ulong state, ulong value)
	{
		ulong x = state ^ unchecked(value + 0x9E3779B97F4A7C15UL);
		x = unchecked((x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL);
		x = unchecked((x ^ (x >> 27)) * 0x94D049BB133111EBUL);
		return x ^ (x >> 31);
	}

	private static bool UnitsEqual(KObject a, KObject b)
	{
		if (ReferenceEquals(a, b)) return true;
		if (!SameUnitType(a.Type, b.Type)) return false;
		if (a.FieldCount != b.FieldCount) return false;
		for (int i = 0; i < a.FieldCount; i++)
		{
			if (!StructuralEquals(a.GetField(i), b.GetField(i))) return false;
		}
		return true;
	}

	// ---------- кортежи (структурные == / hash / compare) ----------

	/// <summary>Структурное равенство кортежей для <c>==</c> (межвидовые числа — как в <see cref="AreEqual"/>).</summary>
	private static bool TuplesEqual(KTuple a, KTuple b)
	{
		if (a.Count != b.Count) return false;
		for (int i = 0; i < a.Count; i++)
		{
			if (!AreEqual(a[i], b[i])) return false;
		}
		return true;
	}

	/// <summary>Структурное равенство кортежей для хэш-контракта (точные виды, рекурсивно).</summary>
	private static bool TuplesStructuralEquals(KTuple a, KTuple b)
	{
		if (a.Count != b.Count) return false;
		for (int i = 0; i < a.Count; i++)
		{
			if (!StructuralEquals(a[i], b[i])) return false;
		}
		return true;
	}

	private static ulong TupleFieldsHash64(KTuple tuple)
	{
		ulong hash = Mix(HashSeed, (ulong)tuple.Count);
		for (int i = 0; i < tuple.Count; i++)
			hash = Mix(hash, StructuralHash64(tuple[i]));
		return hash;
	}

	/// <summary>Лексикографическое сравнение элементов, затем длины; результат -1/0/1.</summary>
	private static int TuplesCompare(KTuple a, KTuple b)
	{
		int shared = Math.Min(a.Count, b.Count);
		for (int i = 0; i < shared; i++)
		{
			int byItem = StructuralCompare(a[i], b[i]);
			if (byItem != 0) return byItem;
		}
		return Math.Sign(a.Count.CompareTo(b.Count));
	}

	/// <summary>
	/// Один и тот же reified-тип: интернированные экземпляры совпадают по ссылке, иначе
	/// сравниваем полное имя и канонические тексты аргументов (фабрики могут быть разными).
	/// </summary>
	private static bool SameUnitType(KClassType a, KClassType b)
	{
		if (ReferenceEquals(a, b)) return true;
		if (!string.Equals(a.Definition.FullName, b.Definition.FullName, StringComparison.Ordinal)) return false;
		if (a.TypeArgs.Length != b.TypeArgs.Length) return false;
		for (int i = 0; i < a.TypeArgs.Length; i++)
		{
			if (!string.Equals(a.TypeArgs[i].Canonical, b.TypeArgs[i].Canonical, StringComparison.Ordinal))
				return false;
		}
		return true;
	}

	public static bool AreEqual(KValue a, KValue b)
	{
		// unit — значение: равенство структурное (spec §9); остальные ссылки — ссылочные.
		if (a.Kind == ValueKind.Ref && b.Kind == ValueKind.Ref
			&& a.Ref is KObject { IsUnit: true } && b.Ref is KObject { IsUnit: true })
			return StructuralEquals(a, b);

		if (a.Kind == ValueKind.Ref && b.Kind == ValueKind.Ref
			&& a.Ref is KTuple tupleA && b.Ref is KTuple tupleB)
			return TuplesEqual(tupleA, tupleB);

		if (a.IsNumber && b.IsNumber) return CompareNumbers(a, b) == 0;
		if (a.Kind != b.Kind) return false;
		return a.Kind switch
		{
			ValueKind.None or ValueKind.Nil or ValueKind.Void => true,
			ValueKind.Bool or ValueKind.Char => a.Bits == b.Bits,
			ValueKind.Str => string.Equals(a.AsStr, b.AsStr, StringComparison.Ordinal),
			ValueKind.Ref => ReferenceEquals(a.Ref, b.Ref),
			_ => a.Bits == b.Bits,
		};
	}

	/// <summary>
	/// Полное (тотальное) сравнение для протокола <c>compare</c> (spec §9/§16), не паникует.
	/// unit — reified-тип, затем поля лексикографически (меньшее число полей при равном
	/// префиксе меньше); числа — численно (межвидовое, как в <see cref="AreEqual"/>);
	/// bool/char — по битам; str — ординально; nil/void/none равны; прочие ссылки — по
	/// идентичности (равные ссылки дают 0); разные виды — по номеру вида (страховка:
	/// binder такие смеси не пропускает). Результат всегда -1, 0 или 1.
	/// </summary>
	public static int StructuralCompare(KValue a, KValue b)
	{
		if (a.Kind == ValueKind.Ref && b.Kind == ValueKind.Ref)
		{
			if (a.Ref is KObject left && b.Ref is KObject right && left.IsUnit && right.IsUnit)
				return ObjectFieldsCompare(left, right);
			if (a.Ref is KTuple tupleA && b.Ref is KTuple tupleB)
				return TuplesCompare(tupleA, tupleB);
			return ReferenceEquals(a.Ref, b.Ref)
				? 0
				: Math.Sign(RuntimeHelpers.GetHashCode(a.Ref).CompareTo(RuntimeHelpers.GetHashCode(b.Ref)));
		}

		if (a.IsNumber && b.IsNumber) return Math.Sign(CompareNumbers(a, b));
		if (a.Kind != b.Kind) return Math.Sign(((int)a.Kind).CompareTo((int)b.Kind));
		return a.Kind switch
		{
			ValueKind.None or ValueKind.Nil or ValueKind.Void => 0,
			ValueKind.Bool or ValueKind.Char => Math.Sign(a.Bits.CompareTo(b.Bits)),
			ValueKind.Str => Math.Sign(string.CompareOrdinal(a.AsStr, b.AsStr)),
			_ => Math.Sign(a.Bits.CompareTo(b.Bits)),
		};
	}

	/// <summary>
	/// Сравнение для производного <c>compare</c> у <c>type</c> (B8a): два объекта
	/// (<c>unit</c> и <c>type</c>) — по reified-типу, затем лексикографически по полям
	/// (через <see cref="StructuralCompare"/>), затем по числу полей. Все прочие сочетания
	/// операндов — ровно как <see cref="StructuralCompare"/>. Не паникует, результат -1/0/1.
	/// </summary>
	public static int FieldCompare(KValue a, KValue b) =>
		a.Kind == ValueKind.Ref && b.Kind == ValueKind.Ref
			&& a.Ref is KObject left && b.Ref is KObject right
				? ObjectFieldsCompare(left, right)
				: StructuralCompare(a, b);

	private static int ObjectFieldsCompare(KObject a, KObject b)
	{
		if (ReferenceEquals(a, b)) return 0;
		int byType = Math.Sign(string.CompareOrdinal(a.Type.Canonical, b.Type.Canonical));
		if (byType != 0) return byType;
		int shared = Math.Min(a.FieldCount, b.FieldCount);
		for (int i = 0; i < shared; i++)
		{
			int byField = StructuralCompare(a.GetField(i), b.GetField(i));
			if (byField != 0) return byField;
		}
		return Math.Sign(a.FieldCount.CompareTo(b.FieldCount));
	}

	/// <summary>
	/// Повтор строки/символа для <c>n * s</c>/<c>s * n</c> (spec §3): один операнд — <c>i64</c>
	/// с числом повторений, второй — <c>str</c> или <c>char</c> (результат всегда <c>str</c>).
	/// </summary>
	public static KValue Repeat(KValue a, KValue b)
	{
		KValue count = a.Kind == ValueKind.I64 ? a : b;
		KValue value = a.Kind == ValueKind.I64 ? b : a;
		if (count.Kind != ValueKind.I64)
			throw new KernPanic($"повтор: ожидалось целое число повторений, получено {count.TypeName}");
		long times = count.AsI64;
		if (times < 0)
			throw new KernPanic("повтор: отрицательное число повторений");
		if (times > 1_000_000)
			throw new KernPanic("повтор: слишком много повторений");
		return value.Kind switch
		{
			ValueKind.Str => KValue.Str(string.Concat(Enumerable.Repeat(value.AsStr, (int)times))),
			ValueKind.Char => KValue.Str(string.Concat(Enumerable.Repeat(value.AsChar.ToString(), (int)times))),
			_ => throw new KernPanic($"повтор не поддерживается для {value.TypeName}"),
		};
	}

	public static bool LessThan(KValue a, KValue b) => Relational(a, b, RelOp.Lt);

	public static bool LessOrEqual(KValue a, KValue b) => Relational(a, b, RelOp.Le);

	public static bool GreaterThan(KValue a, KValue b) => Relational(a, b, RelOp.Gt);

	public static bool GreaterOrEqual(KValue a, KValue b) => Relational(a, b, RelOp.Ge);

	private enum RelOp { Lt, Le, Gt, Ge }

	private static bool Relational(KValue a, KValue b, RelOp op)
	{
		if (a.IsNumber && b.IsNumber)
		{
			if (a.IsFloat || b.IsFloat)
			{
				double x = a.AsF64;
				double y = b.AsF64;
				return op switch
				{
					RelOp.Lt => x < y,
					RelOp.Le => x <= y,
					RelOp.Gt => x > y,
					_ => x >= y,
				};
			}
			return Apply(CompareIntegers(a, b), op);
		}

		if (a.Kind == ValueKind.Str && b.Kind == ValueKind.Str)
			return Apply(string.CompareOrdinal(a.AsStr, b.AsStr), op);
		if (a.Kind == ValueKind.Char && b.Kind == ValueKind.Char)
			return Apply(a.AsChar.CompareTo(b.AsChar), op);
		throw new KernPanic($"сравнение {a.TypeName} и {b.TypeName} не поддерживается");
	}

	private static bool Apply(int comparison, RelOp op) => op switch
	{
		RelOp.Lt => comparison < 0,
		RelOp.Le => comparison <= 0,
		RelOp.Gt => comparison > 0,
		_ => comparison >= 0,
	};

	private static int CompareNumbers(KValue a, KValue b)
	{
		if (a.IsFloat || b.IsFloat) return a.AsF64.CompareTo(b.AsF64);
		return CompareIntegers(a, b);
	}

	private static int CompareIntegers(KValue a, KValue b)
	{
		if (a.Kind == b.Kind) return a.Bits.CompareTo(b.Bits);
		if (a.IsUnsignedInteger && b.IsUnsignedInteger) return a.AsU64.CompareTo(b.AsU64);
		if (a.IsSignedInteger && b.IsSignedInteger) return a.Bits.CompareTo(b.Bits);
		// смешанные знаковые/беззнаковые: отрицательное всегда меньше
		if (a.IsSignedInteger) return a.Bits < 0 ? -1 : unchecked((ulong)a.Bits).CompareTo(b.AsU64);
		return b.Bits < 0 ? 1 : a.AsU64.CompareTo(unchecked((ulong)b.Bits));
	}

	// ---------- логика ----------

	public static KValue Not(KValue value) => KValue.Bool(!RequireBool(value));

	public static KValue And(KValue a, KValue b) => KValue.Bool(RequireBool(a) && RequireBool(b));

	public static KValue Or(KValue a, KValue b) => KValue.Bool(RequireBool(a) || RequireBool(b));

	private static bool RequireBool(KValue value) =>
		value.Kind == ValueKind.Bool ? value.AsBool : throw new KernPanic($"ожидалось bool, получено {value.TypeName}");

	// ---------- преобразования ----------

	/// <summary>
	/// Приведение к целевому виду. Целые усекаются/wrap; дробное->целое — C#-приведение
	/// (вне диапазона значение зависит от платформы — см. DECISIONS_NEEDED, семантика не зафиксирована).
	/// </summary>
	public static KValue Convert(KValue value, ValueKind target)
	{
		if (value.Kind == target) return value;

		if (target == ValueKind.Bool)
		{
			if (value.Kind == ValueKind.Bool) return value;
			if (value.IsNumber) return KValue.Bool(value.Bits != 0);
			throw new KernPanic($"преобразование {value.TypeName} в bool невозможно");
		}

		if (target != ValueKind.Char && !IsNumericKind(target))
			throw new KernPanic($"преобразование в {KindName(target)} не поддерживается");
		if (!value.IsNumber && value.Kind != ValueKind.Char)
			throw new KernPanic($"преобразование {value.TypeName} в {KindName(target)} невозможно");

		if (target == ValueKind.Char)
			return KValue.Char((char)unchecked((ushort)ToInt64(value)));

		long bits = ToInt64(value);
		return target switch
		{
			ValueKind.I8 => KValue.I8(unchecked((sbyte)bits)),
			ValueKind.I16 => KValue.I16(unchecked((short)bits)),
			ValueKind.I32 => KValue.I32(unchecked((int)bits)),
			ValueKind.I64 => KValue.I64(bits),
			ValueKind.U8 => KValue.U8(unchecked((byte)bits)),
			ValueKind.U16 => KValue.U16(unchecked((ushort)bits)),
			ValueKind.U32 => KValue.U32(unchecked((uint)bits)),
			ValueKind.U64 => KValue.U64(unchecked((ulong)bits)),
			ValueKind.F16 => KValue.F16((Half)ToDouble(value)),
			ValueKind.F32 => KValue.F32((float)ToDouble(value)),
			_ => KValue.F64(ToDouble(value)),
		};
	}

	private static bool IsNumericKind(ValueKind kind) =>
		(byte)kind >= (byte)ValueKind.I8 && (byte)kind <= (byte)ValueKind.F64;

	private static long ToInt64(KValue value) => value.Kind switch
	{
		ValueKind.Char => value.AsChar,
		ValueKind.I8 or ValueKind.I16 or ValueKind.I32 or ValueKind.I64 => value.Bits,
		ValueKind.U8 or ValueKind.U16 or ValueKind.U32 or ValueKind.U64 => unchecked((long)value.AsU64),
		ValueKind.F16 or ValueKind.F32 or ValueKind.F64 => (long)value.AsF64,
		_ => throw new KernPanic($"преобразование {value.TypeName} в целое невозможно"),
	};

	private static double ToDouble(KValue value) => value.Kind == ValueKind.Char
		? value.AsChar
		: value.IsNumber
			? value.AsF64
			: throw new KernPanic($"преобразование {value.TypeName} в дробное невозможно");

	private static string KindName(ValueKind kind) => new KValue(kind, 0).TypeName;
}
