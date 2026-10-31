using Kern.Binding.Attributes;
using Kern.Runtime.Numerics;
using SMath = System.Math;

namespace Kern.Runtime.Math;

/// <summary>Статические функции модуля kern.math (compare System.Math).</summary>
[KernType("math")]
public static class MathApi
{
	private const KernConstraintKind N = KernConstraintKind.Num;

	[KernStatic] public static readonly double Pi = SMath.PI;
	[KernStatic] public static readonly double E = SMath.E;
	[KernStatic] public static readonly double Tau = SMath.Tau;

	// ---------- универсальные числовые ----------
	[KernMember] public static T Abs<[KernConstraint(N)] T>(T value) => NumApi.Abs(value);
	[KernMember] public static T Min<[KernConstraint(N)] T>(T a, T b) => NumApi.Min(a, b);
	[KernMember] public static T Max<[KernConstraint(N)] T>(T a, T b) => NumApi.Max(a, b);
	[KernMember] public static T Clamp<[KernConstraint(N)] T>(T value, T low, T high) => NumApi.Clamp(value, low, high);
	[KernMember] public static int Sign(double value) => double.IsNaN(value) ? 0 : SMath.Sign(value);

	// ---------- степени и логарифмы ----------
	[KernMember] public static double Pow(double value, double exponent) => SMath.Pow(value, exponent);
	[KernMember] public static double Sqrt(double value) => SMath.Sqrt(value);
	[KernMember] public static double Cbrt(double value) => SMath.Cbrt(value);
	[KernMember] public static double Exp(double value) => SMath.Exp(value);
	[KernMember] public static double Log(double value) => SMath.Log(value);
	[KernMember] public static double Log2(double value) => SMath.Log2(value);
	[KernMember] public static double Log10(double value) => SMath.Log10(value);
	[KernMember] public static double LogBase(double value, double radix) => SMath.Log(value, radix);
	[KernMember] public static double Hypot(double a, double b) => double.Hypot(a, b);

	// ---------- округление ----------
	[KernMember] public static double Floor(double value) => SMath.Floor(value);
	[KernMember] public static double Ceil(double value) => SMath.Ceiling(value);
	[KernMember] public static double Round(double value) => SMath.Round(value, MidpointRounding.AwayFromZero);
	[KernMember] public static double Truncate(double value) => SMath.Truncate(value);

	// ---------- тригонометрия ----------
	[KernMember] public static double Sin(double value) => SMath.Sin(value);
	[KernMember] public static double Cos(double value) => SMath.Cos(value);
	[KernMember] public static double Tan(double value) => SMath.Tan(value);
	[KernMember] public static double Asin(double value) => SMath.Asin(value);
	[KernMember] public static double Acos(double value) => SMath.Acos(value);
	[KernMember] public static double Atan(double value) => SMath.Atan(value);
	[KernMember] public static double Atan2(double y, double x) => SMath.Atan2(y, x);
	[KernMember] public static double Sinh(double value) => SMath.Sinh(value);
	[KernMember] public static double Cosh(double value) => SMath.Cosh(value);
	[KernMember] public static double Tanh(double value) => SMath.Tanh(value);
	[KernMember] public static double ToRadians(double degrees) => degrees * SMath.PI / 180.0;
	[KernMember] public static double ToDegrees(double radians) => radians * 180.0 / SMath.PI;

	// ---------- целочисленная теория чисел ----------
	[KernMember] public static long Gcd(long a, long b)
	{
		(a, b) = (SMath.Abs(a), SMath.Abs(b));
		while (b != 0) (a, b) = (b, a % b);
		return a;
	}

	[KernMember] public static long Lcm(long a, long b) => a == 0 || b == 0 ? 0 : SMath.Abs(a / Gcd(a, b) * b);

	[KernMember] public static long Factorial(int n)
	{
		if (n is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(n), "факториал определён для 0..20 (i64)");
		long r = 1;
		for (int i = 2; i <= n; i++) r *= i;
		return r;
	}

	[KernMember] public static bool IsPrime(long n)
	{
		if (n < 2) return false;
		if (n < 4) return true;
		if (n % 2 == 0 || n % 3 == 0) return false;
		for (long i = 5; i * i <= n; i += 6)
			if (n % i == 0 || n % (i + 2) == 0) return false;
		return true;
	}
}
