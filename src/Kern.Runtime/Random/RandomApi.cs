using System.Security.Cryptography;
using Kern.Binding.Attributes;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Random;

/// <summary>
/// <c>kern.random</c> (spec §16): криптостойкие значения по умолчанию и явный
/// детерминированный генератор <c>rng</c>. Один seed даёт одну и ту же
/// последовательность (xorshift64), поэтому им можно пользоваться в тестах.
/// </summary>
[KernModule("kern.random")]
public static class RandomApi
{
	private const int MaxBytes = 1 << 20;

	/// <summary><c>random.i32()</c>: любой <c>i32</c>.</summary>
	[KernMember]
	[KernName("i32")]
	public static int I32() => BitConverter.ToInt32(SecureBytes(4));

	/// <summary><c>random.i32(max)</c>: <c>[0, max)</c>.</summary>
	[KernMember]
	[KernName("i32")]
	public static int I32(int max)
	{
		RequireRange("random.i32", 0, max);
		return RandomNumberGenerator.GetInt32(max);
	}

	/// <summary><c>random.i32(min, max)</c>: <c>[min, max)</c>.</summary>
	[KernMember]
	[KernName("i32")]
	public static int I32(int min, int max)
	{
		RequireRange("random.i32", min, max);
		return RandomNumberGenerator.GetInt32(min, max);
	}

	/// <summary><c>random.i64()</c>: любой <c>i64</c>.</summary>
	[KernMember]
	[KernName("i64")]
	public static long I64() => BitConverter.ToInt64(SecureBytes(8));

	/// <summary><c>random.i64(max)</c>: <c>[0, max)</c>.</summary>
	[KernMember]
	[KernName("i64")]
	public static long I64(long max) => SecureI64(0, max);

	/// <summary><c>random.i64(min, max)</c>: <c>[min, max)</c>.</summary>
	[KernMember]
	[KernName("i64")]
	public static long I64(long min, long max) => SecureI64(min, max);

	/// <summary><c>random.f64()</c>: <c>[0, 1)</c>.</summary>
	[KernMember]
	[KernName("f64")]
	public static double F64() => (BitConverter.ToUInt64(SecureBytes(8)) >> 11) * (1.0 / (1UL << 53));

	/// <summary><c>random.bool()</c>: равновероятные <c>true</c>/<c>false</c>.</summary>
	[KernMember]
	[KernName("bool")]
	public static bool Bool() => (SecureBytes(1)[0] & 1) != 0;

	/// <summary><c>random.bytes(n)</c>: n криптостойких байт.</summary>
	[KernMember]
	[KernName("bytes")]
	[KernReturn("list<u8>")]
	public static KValue Bytes(Interpreter interpreter, int count)
	{
		byte[] bytes = SecureBytes(RequireCount("random.bytes", count));
		KType element = interpreter.Types.Prim(PrimKind.U8);
		return KValue.Obj(new KList(element, bytes.Select(KValue.U8)));
	}

	/// <summary><c>random.hex(n)</c>: 2n hex-символов (нижний регистр).</summary>
	[KernMember]
	[KernName("hex")]
	public static string Hex(int count) =>
		Convert.ToHexString(SecureBytes(RequireCount("random.hex", count))).ToLowerInvariant();

	/// <summary><c>random.choice(items)</c>: случайный элемент; пустой список — panic.</summary>
	[KernMember]
	[KernName("choice")]
	[KernReturn("T")]
	public static KValue Choice<T>([KernParameterType("list<T>")] KValue items) =>
		Choose(items, SecureIndex);

	/// <summary><c>random.shuffle(items)</c>: копия в случайном порядке.</summary>
	[KernMember]
	[KernName("shuffle")]
	[KernReturn("list<T>")]
	public static KValue Shuffle<T>([KernParameterType("list<T>")] KValue items) =>
		ShuffleList(items, SecureIndex);

	/// <summary><c>random.sample(items, n)</c>: n разных элементов в случайном порядке.</summary>
	[KernMember]
	[KernName("sample")]
	[KernReturn("list<T>")]
	public static KValue Sample<T>([KernParameterType("list<T>")] KValue items, int count) =>
		SampleList(items, count, SecureIndex);

	/// <summary><c>random.rng(seed)</c>: детерминированный генератор.</summary>
	[KernMember]
	[KernName("rng")]
	[KernReturn("rng")]
	public static KValue Rng(long seed) => KValue.Obj(new KNativeBox(new KernRng(seed)));

	private static byte[] SecureBytes(int count)
	{
		var bytes = new byte[count];
		RandomNumberGenerator.Fill(bytes);
		return bytes;
	}

	private static int SecureIndex(int count) => RandomNumberGenerator.GetInt32(count);

	private static long SecureI64(long min, long max)
	{
		RequireRange("random.i64", min, max);
		Int128 span = (Int128)max - min;
		if (span > long.MaxValue)
			throw new KernPanic("random.i64: диапазон слишком велик");
		ulong range = (ulong)span;
		ulong limit = ulong.MaxValue - (ulong.MaxValue % range);
		while (true)
		{
			ulong value = BitConverter.ToUInt64(SecureBytes(8));
			if (value < limit)
				return (long)((Int128)min + (long)(value % range));
		}
	}

	private static void RequireRange(string name, long min, long max)
	{
		if (min >= max)
			throw new KernPanic($"{name}: нужен диапазон min < max, получено {min}..{max}");
	}

	private static int RequireCount(string name, int count)
	{
		if (count < 0 || count > MaxBytes)
			throw new KernPanic($"{name}: количество должно быть 0..{MaxBytes}, получено {count}");
		return count;
	}

	internal static KList RequireList(string name, KValue value) => value.Ref switch
	{
		KList list => list,
		_ => throw new KernPanic($"{name}: ожидался list, получено {value.TypeName}"),
	};

	private static KValue Choose(KValue items, Func<int, int> index)
	{
		KList list = RequireList("random.choice", items);
		if (list.Count == 0)
			throw new KernPanic("random.choice: пустой список");
		return list[index(list.Count)];
	}

	internal static KValue ShuffleList(KValue items, Func<int, int> index)
	{
		KList list = RequireList("random.shuffle", items);
		KValue[] copy = [.. list.Items];
		for (var position = copy.Length - 1; position > 0; position--)
		{
			int other = index(position + 1);
			(copy[position], copy[other]) = (copy[other], copy[position]);
		}

		return KValue.Obj(new KList(list.ElementType, copy));
	}

	internal static KValue SampleList(KValue items, int count, Func<int, int> index)
	{
		KList list = RequireList("random.sample", items);
		if (count < 0 || count > list.Count)
			throw new KernPanic($"random.sample: n должно быть 0..{list.Count}, получено {count}");
		KValue[] copy = [.. list.Items];
		for (var position = 0; position < count; position++)
		{
			int other = position + index(copy.Length - position);
			(copy[position], copy[other]) = (copy[other], copy[position]);
		}

		return KValue.Obj(new KList(list.ElementType, copy.Take(count)));
	}
}

/// <summary>Детерминированный генератор <c>rng</c> (xorshift64).</summary>
[KernType("rng")]
public sealed class KernRng(long seed)
{
	// Ноль — неподвижная точка xorshift64: заменяем на нечётную константу.
	private ulong _state = seed == 0 ? 0x9E3779B97F4A7C15UL : unchecked((ulong)seed);

	public ulong NextU64()
	{
		_state ^= _state << 13;
		_state ^= _state >> 7;
		_state ^= _state << 17;
		return _state;
	}

	public int NextI32() => (int)NextU64();

	public int NextI32(int max)
	{
		RequireRange(0, max);
		return (int)(NextU64() % (ulong)max);
	}

	public int NextI32(int min, int max)
	{
		RequireRange(min, max);
		return (int)(min + (long)(NextU64() % (ulong)((long)max - min)));
	}

	public int Index(int count)
	{
		if (count <= 0)
			throw new KernPanic("random.rng: пустой диапазон");
		return (int)(NextU64() % (ulong)count);
	}

	public long NextI64() => (long)NextU64();

	public double NextF64() => (NextU64() >> 11) * (1.0 / (1UL << 53));

	public bool NextBool() => (NextU64() & 1) != 0;

	private static void RequireRange(long min, long max)
	{
		if (min >= max)
			throw new KernPanic($"random.rng: нужен диапазон min < max, получено {min}..{max}");
	}
}

/// <summary>Члены <c>rng</c>: те же операции, что у модуля, но детерминированные.</summary>
[KernType("rng")]
public static class RngApi
{
	[KernMember]
	[KernName("next_i32")]
	public static int NextI32(KNativeBox self) => Require(self).NextI32();

	[KernMember]
	[KernName("next_i32")]
	public static int NextI32(KNativeBox self, int max) => Require(self).NextI32(max);

	[KernMember]
	[KernName("next_i32")]
	public static int NextI32(KNativeBox self, int min, int max) => Require(self).NextI32(min, max);

	[KernMember]
	[KernName("next_i64")]
	public static long NextI64(KNativeBox self) => Require(self).NextI64();

	[KernMember]
	[KernName("next_f64")]
	public static double NextF64(KNativeBox self) => Require(self).NextF64();

	[KernMember]
	[KernName("next_bool")]
	public static bool NextBool(KNativeBox self) => Require(self).NextBool();

	[KernMember]
	[KernName("choice")]
	[KernReturn("T")]
	public static KValue Choice<T>(KNativeBox self, [KernParameterType("list<T>")] KValue items) =>
		Choose(self, items);

	[KernMember]
	[KernName("shuffle")]
	[KernReturn("list<T>")]
	public static KValue Shuffle<T>(KNativeBox self, [KernParameterType("list<T>")] KValue items) =>
		RandomApi.ShuffleList(items, Require(self).Index);

	[KernMember]
	[KernName("sample")]
	[KernReturn("list<T>")]
	public static KValue Sample<T>(
		KNativeBox self,
		[KernParameterType("list<T>")] KValue items,
		int count) =>
		RandomApi.SampleList(items, count, Require(self).Index);

	private static KValue Choose(KNativeBox self, KValue items)
	{
		KList list = items.Ref switch
		{
			KList value => value,
			_ => throw new KernPanic($"random.rng.choice: ожидался list, получено {items.TypeName}"),
		};
		if (list.Count == 0)
			throw new KernPanic("random.rng.choice: пустой список");
		return list[Require(self).Index(list.Count)];
	}

	private static KernRng Require(KNativeBox self) => self.Value switch
	{
		KernRng rng => rng,
		_ => throw new KernPanic($"rng: ожидался генератор, получено {self.Value.GetType().Name}"),
	};
}
