using System.Globalization;
using System.Numerics;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Values;

namespace Kern.Runtime.Numerics;

/// <summary>Целое произвольной длины (System.Numerics.BigInteger). Хранится в KNativeBox.</summary>
[KernType("bigint")]
public static class BigIntApi
{
	// ---------- создание ----------
	[KernStatic] public static BigInteger Parse(string text) => BigInteger.Parse(text.Trim(), CultureInfo.InvariantCulture);
	[KernStatic] public static BigInteger FromI64(long value) => value;
	[KernStatic] public static KResult<BigInteger, string> TryParse(string text) =>
		BigInteger.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
			? Kv.Ok<BigInteger>(KValue.Obj(new KNativeBox(v))) : Kv.Err<BigInteger>($"не bigint: \"{text}\"");

	// ---------- свойства ----------
	[KernProperty] public static BigInteger Abs(BigInteger self) => BigInteger.Abs(self);
	[KernProperty] public static int Sign(BigInteger self) => self.Sign;
	[KernProperty] public static bool IsEven(BigInteger self) => self.IsEven;
	[KernProperty] public static bool IsOdd(BigInteger self) => !self.IsEven;
	[KernProperty] public static bool IsZero(BigInteger self) => self.IsZero;
	[KernProperty] public static long BitLength(BigInteger self) => self.GetBitLength();

	// ---------- операторы ----------
	[KernOperator("+")] public static BigInteger Add(BigInteger self, BigInteger other) => self + other;
	[KernOperator("-")] public static BigInteger Sub(BigInteger self, BigInteger other) => self - other;
	[KernOperator("*")] public static BigInteger Mul(BigInteger self, BigInteger other) => self * other;
	[KernOperator("/")] public static BigInteger Div(BigInteger self, BigInteger other) => self / other;
	[KernOperator("%")] public static BigInteger Rem(BigInteger self, BigInteger other) => self % other;
	[KernOperator("==")] public static bool Eq(BigInteger self, BigInteger other) => self == other;
	[KernOperator("<")] public static bool Lt(BigInteger self, BigInteger other) => self < other;
	[KernOperator("<=>")] public static int Cmp(BigInteger self, BigInteger other) => self.CompareTo(other);

	// ---------- методы ----------
	[KernMember] public static BigInteger Pow(BigInteger self, int exp) => BigInteger.Pow(self, exp);
	[KernMember] public static BigInteger ModPow(BigInteger self, BigInteger exp, BigInteger mod) => BigInteger.ModPow(self, exp, mod);
	[KernMember] public static BigInteger Gcd(BigInteger self, BigInteger other) => BigInteger.GreatestCommonDivisor(self, other);
	[KernMember] public static BigInteger Min(BigInteger self, BigInteger other) => BigInteger.Min(self, other);
	[KernMember] public static BigInteger Max(BigInteger self, BigInteger other) => BigInteger.Max(self, other);
	[KernMember] public static int CompareTo(BigInteger self, BigInteger other) => self.CompareTo(other);
	[KernMember] public static string ToStr(BigInteger self) => self.ToString(CultureInfo.InvariantCulture);
	[KernMember] public static string ToStr(BigInteger self, int radix) => radix == 10 ? self.ToString(CultureInfo.InvariantCulture) : ToRadix(self, radix);
	[KernMember] public static long ToI64(BigInteger self) => (long)self;
	[KernMember] public static double ToF64(BigInteger self) => (double)self;

	private static string ToRadix(BigInteger v, int radix)
	{
		if (radix is < 2 or > 36) throw new ArgumentOutOfRangeException(nameof(radix), "основание 2..36");
		if (v.IsZero) return "0";
		var digits = new System.Text.StringBuilder();
		var n = BigInteger.Abs(v);
		while (!n.IsZero)
		{
			int d = (int)(n % radix);
			digits.Insert(0, (char)(d < 10 ? '0' + d : 'a' + d - 10));
			n /= radix;
		}
		return (v.Sign < 0 ? "-" : "") + digits;
	}
}
