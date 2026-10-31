using System.Numerics;
using Kern.Binding.Attributes;
using Kern.Vm.Values;

namespace Kern.Runtime.Numerics;

/// <summary>Члены целых типов i8..u64. Арифметика оборачивается по ширине (wrap), как unchecked в C#.</summary>
[KernType("inum")]
public static class IntApi
{
	private const KernConstraintKind I = KernConstraintKind.Inum;

	[KernProperty] public static bool IsEven<[KernConstraint(I)] T>(T self) => (NumCast.In(self).Bits & 1) == 0;
	[KernProperty] public static bool IsOdd<[KernConstraint(I)] T>(T self) => (NumCast.In(self).Bits & 1) != 0;

	/// <summary>Возведение в неотрицательную целую степень с wrap по ширине.</summary>
	[KernMember] public static T Pow<[KernConstraint(I)] T>(T self, int exp)
	{
		if (exp < 0) throw new ArgumentOutOfRangeException(nameof(exp), "отрицательная степень для целого");
		var v = NumCast.In(self);
		long result = 1, b = v.Bits;
		for (int e = exp; e > 0; e >>= 1, b = unchecked(b * b))
			if ((e & 1) != 0) result = unchecked(result * b);
		return NumCast.Out<T>(Num.Wrap(v.Kind, result));
	}

	[KernMember] public static T Gcd<[KernConstraint(I)] T>(T self, T other)
	{
		var a = NumCast.In(self);
		long x = System.Math.Abs(a.Bits), y = System.Math.Abs(NumCast.In(other).Bits);
		while (y != 0) (x, y) = (y, x % y);
		return NumCast.Out<T>(Num.Wrap(a.Kind, x));
	}

	/// <summary>Делится ли self на other.</summary>
	[KernMember] public static bool DivisibleBy<[KernConstraint(I)] T>(T self, T other) =>
		NumCast.In(other).Bits is var d && d != 0 && NumCast.In(self).Bits % d == 0;

	[KernProperty] public static int PopCount<[KernConstraint(I)] T>(T self) => BitOperations.PopCount(Masked(NumCast.In(self)));
	[KernProperty] public static int LeadingZeros<[KernConstraint(I)] T>(T self)
	{
		var v = NumCast.In(self);
		return BitOperations.LeadingZeroCount(Masked(v)) - (64 - Num.BitsOf(v.Kind));
	}
	[KernProperty] public static int TrailingZeros<[KernConstraint(I)] T>(T self)
	{
		var v = NumCast.In(self);
		ulong m = Masked(v);
		return m == 0 ? Num.BitsOf(v.Kind) : BitOperations.TrailingZeroCount(m);
	}

	/// <summary>Значение без знакового расширения: младшие BitsOf(kind) бит.</summary>
	private static ulong Masked(KValue v) => Num.BitsOf(v.Kind) switch
	{
		64 => unchecked((ulong)v.Bits),
		int bits => unchecked((ulong)v.Bits) & ((1UL << bits) - 1),
	};

	// ---------- с проверкой переполнения: result вместо wrap ----------
	[KernMember] public static T CheckedAdd<[KernConstraint(I)] T>(T self, T other) => Checked(self, other, '+');
	[KernMember] public static T CheckedSub<[KernConstraint(I)] T>(T self, T other) => Checked(self, other, '-');
	[KernMember] public static T CheckedMul<[KernConstraint(I)] T>(T self, T other) => Checked(self, other, '*');

	private static T Checked<T>(T a, T b, char op)
	{
		var x = NumCast.In(a);
		var y = NumCast.In(b);
		var kind = Num.ResultInt(x.Kind, y.Kind);
		var big = op switch
		{
			'+' => Big(x) + Big(y),
			'-' => Big(x) - Big(y),
			_ => Big(x) * Big(y),
		};
		var wrapped = Num.Wrap(kind, unchecked((long)(ulong)(big & ulong.MaxValue)));
		if (Big(wrapped) != big) throw new OverflowException($"переполнение {kind}: {x.Bits} {op} {y.Bits}");
		return NumCast.Out<T>(wrapped);
	}

	private static BigInteger Big(KValue v) => v.IsUnsigned ? (BigInteger)v.AsU64 : v.Bits;
}
