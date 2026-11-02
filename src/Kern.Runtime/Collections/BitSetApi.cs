using System.Numerics;
using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Набор битов <c>bitset</c> (spec §16): фиксированная длина, операции над отдельными
/// битами и побитовые операции над двумя наборами. Внутри — массив слов <c>u64</c>;
/// неиспользуемые старшие биты последнего слова всегда нули.
/// </summary>
[KernType("bitset")]
public sealed class KernBitSet
{
	private readonly ulong[] _words;
	private readonly int _size;

	/// <summary>Создать набор из <paramref name="size"/> нулевых битов.</summary>
	public KernBitSet(int size)
	{
		if (size < 0)
			throw new KernPanic($"bitset: размер {size} не может быть отрицательным");
		_size = size;
		_words = new ulong[(size + 63) / 64];
	}

	/// <summary>Число битов (длина набора).</summary>
	public int Size => _size;

	/// <summary>Число установленных битов.</summary>
	public int Count
	{
		get
		{
			int total = 0;
			foreach (ulong word in _words)
				total += BitOperations.PopCount(word);
			return total;
		}
	}

	/// <summary>Установить бит.</summary>
	public void Set(int index)
	{
		Validate(index);
		_words[index / 64] |= 1UL << (index % 64);
	}

	/// <summary>Сбросить бит.</summary>
	public void ClearBit(int index)
	{
		Validate(index);
		_words[index / 64] &= ~(1UL << (index % 64));
	}

	/// <summary>Инвертировать бит.</summary>
	public void Flip(int index)
	{
		Validate(index);
		_words[index / 64] ^= 1UL << (index % 64);
	}

	/// <summary>Значение бита.</summary>
	public bool Get(int index)
	{
		Validate(index);
		return (_words[index / 64] & (1UL << (index % 64))) != 0;
	}

	/// <summary>Сбросить все биты.</summary>
	public void ClearAll() => Array.Clear(_words);

	/// <summary>Побитовое «и»; размеры должны совпадать.</summary>
	public KernBitSet And(KernBitSet other)
	{
		RequireSameSize(other);
		var result = new KernBitSet(_size);
		for (var index = 0; index < _words.Length; index++)
			result._words[index] = _words[index] & other._words[index];
		return result;
	}

	/// <summary>Побитовое «или»; размеры должны совпадать.</summary>
	public KernBitSet Or(KernBitSet other)
	{
		RequireSameSize(other);
		var result = new KernBitSet(_size);
		for (var index = 0; index < _words.Length; index++)
			result._words[index] = _words[index] | other._words[index];
		return result;
	}

	/// <summary>Побитовое исключающее «или»; размеры должны совпадать.</summary>
	public KernBitSet Xor(KernBitSet other)
	{
		RequireSameSize(other);
		var result = new KernBitSet(_size);
		for (var index = 0; index < _words.Length; index++)
			result._words[index] = _words[index] ^ other._words[index];
		return result;
	}

	/// <summary>Инверсия в пределах длины набора.</summary>
	public KernBitSet Not()
	{
		var result = new KernBitSet(_size);
		for (var index = 0; index < _words.Length; index++)
			result._words[index] = ~_words[index];
		result.Trim();
		return result;
	}

	/// <summary>Проверка индекса бита.</summary>
	private void Validate(int index)
	{
		if ((uint)index >= (uint)_size)
			throw new KernPanic($"bitset: индекс {index} вне диапазона 0..{_size - 1}");
	}

	private void RequireSameSize(KernBitSet other)
	{
		if (other._size != _size)
			throw new KernPanic($"bitset: размеры не совпадают ({_size} и {other._size})");
	}

	/// <summary>Обнулить неиспользуемые старшие биты последнего слова.</summary>
	private void Trim()
	{
		int remainder = _size % 64;
		if (remainder != 0 && _words.Length > 0)
			_words[^1] &= (1UL << remainder) - 1;
	}

	public override string ToString() => $"bitset({_size}, {Count})";
}

/// <summary>
/// Фабрика и члены <c>bitset</c> (spec §16): <c>set(i)</c>, <c>clear(i)</c>,
/// <c>flip(i)</c>, <c>get(i) -&gt; bool</c>, свойства <c>count</c>/<c>len</c>,
/// <c>clear_all()</c>, <c>and/or/xor(other)</c>, <c>not()</c>.
/// </summary>
[KernType("bitset")]
public static class BitSetApi
{
	/// <summary><c>bitset(size)</c>: набор из <paramref name="size"/> нулевых битов.</summary>
	[KernCtor]
	[KernName("bitset")]
	[KernReturn("bitset")]
	public static KValue Create(int size) => KValue.Obj(new KNativeBox(new KernBitSet(size)));

	/// <summary><c>len</c>: длина набора в битах.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Size;

	/// <summary><c>count</c>: число установленных битов.</summary>
	[KernProperty]
	public static int Count(KNativeBox self) => Require(self).Count;

	/// <summary><c>set(i)</c>: установить бит.</summary>
	[KernMember]
	public static void Set(KNativeBox self, int index) => Require(self).Set(index);

	/// <summary><c>clear(i)</c>: сбросить бит.</summary>
	[KernMember]
	public static void Clear(KNativeBox self, int index) => Require(self).ClearBit(index);

	/// <summary><c>flip(i)</c>: инвертировать бит.</summary>
	[KernMember]
	public static void Flip(KNativeBox self, int index) => Require(self).Flip(index);

	/// <summary><c>get(i) -&gt; bool</c>: значение бита.</summary>
	[KernMember]
	public static bool Get(KNativeBox self, int index) => Require(self).Get(index);

	/// <summary><c>clear_all()</c>: сбросить все биты.</summary>
	[KernName("clear_all")]
	[KernMember]
	public static void ClearAll(KNativeBox self) => Require(self).ClearAll();

	/// <summary><c>and(other) -&gt; bitset</c>: побитовое «и».</summary>
	[KernMember]
	[KernReturn("bitset")]
	public static KValue And(KNativeBox self, KValue other) =>
		KValue.Obj(new KNativeBox(Require(self).And(RequireValue(other))));

	/// <summary><c>or(other) -&gt; bitset</c>: побитовое «или».</summary>
	[KernMember]
	[KernReturn("bitset")]
	public static KValue Or(KNativeBox self, KValue other) =>
		KValue.Obj(new KNativeBox(Require(self).Or(RequireValue(other))));

	/// <summary><c>xor(other) -&gt; bitset</c>: побитовое исключающее «или».</summary>
	[KernMember]
	[KernReturn("bitset")]
	public static KValue Xor(KNativeBox self, KValue other) =>
		KValue.Obj(new KNativeBox(Require(self).Xor(RequireValue(other))));

	/// <summary><c>not() -&gt; bitset</c>: инверсия в пределах длины.</summary>
	[KernMember]
	[KernReturn("bitset")]
	public static KValue Not(KNativeBox self) => KValue.Obj(new KNativeBox(Require(self).Not()));

	private static KernBitSet Require(KNativeBox self) => self.Value switch
	{
		KernBitSet bits => bits,
		_ => throw new KernPanic($"ожидался bitset, получено {self.Value.GetType().Name}"),
	};

	private static KernBitSet RequireValue(KValue value) => value.Ref switch
	{
		KNativeBox { Value: KernBitSet bits } => bits,
		_ => throw new KernPanic($"bitset: ожидался bitset, получено {value.TypeName}"),
	};
}
