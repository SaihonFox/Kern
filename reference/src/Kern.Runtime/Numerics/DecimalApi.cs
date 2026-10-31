using System.Globalization;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Values;

namespace Kern.Runtime.Numerics;

/// <summary>Десятичное число с фиксированной точкой (System.Decimal). Хранится в KNativeBox.</summary>
[KernType("decimal")]
public static class DecimalApi
{
	[KernStatic] public static decimal Parse(string text) => decimal.Parse(text.Trim(), CultureInfo.InvariantCulture);
	[KernStatic] public static KResult<decimal, string> TryParse(string text) =>
		decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v)
			? Kv.Ok<decimal>(KValue.Obj(new KNativeBox(v))) : Kv.Err<decimal>($"не decimal: \"{text}\"");

	[KernProperty] public static decimal Abs(decimal self) => System.Math.Abs(self);
	[KernProperty] public static int Sign(decimal self) => System.Math.Sign(self);
	[KernProperty] public static decimal Floor(decimal self) => System.Math.Floor(self);
	[KernProperty] public static decimal Ceil(decimal self) => System.Math.Ceiling(self);
	[KernProperty] public static decimal Truncate(decimal self) => System.Math.Truncate(self);
	[KernProperty] public static decimal Round(decimal self) => System.Math.Round(self, MidpointRounding.AwayFromZero);
	[KernProperty] public static int Scale(decimal self) => (decimal.GetBits(self)[3] >> 16) & 0xFF;

	[KernOperator("+")] public static decimal Add(decimal self, decimal other) => self + other;
	[KernOperator("-")] public static decimal Sub(decimal self, decimal other) => self - other;
	[KernOperator("*")] public static decimal Mul(decimal self, decimal other) => self * other;
	[KernOperator("/")] public static decimal Div(decimal self, decimal other) => self / other;
	[KernOperator("%")] public static decimal Rem(decimal self, decimal other) => self % other;
	[KernOperator("==")] public static bool Eq(decimal self, decimal other) => self == other;
	[KernOperator("<")] public static bool Lt(decimal self, decimal other) => self < other;
	[KernOperator("<=>")] public static int Cmp(decimal self, decimal other) => self.CompareTo(other);

	[KernMember] public static decimal RoundTo(decimal self, int digits) =>
		System.Math.Round(self, System.Math.Clamp(digits, 0, 28), MidpointRounding.AwayFromZero);
	[KernMember] public static decimal Min(decimal self, decimal other) => System.Math.Min(self, other);
	[KernMember] public static decimal Max(decimal self, decimal other) => System.Math.Max(self, other);
	[KernMember] public static decimal Pow(decimal self, int exp)
	{
		decimal r = 1;
		for (int i = 0; i < System.Math.Abs(exp); i++) r *= self;
		return exp < 0 ? 1 / r : r;
	}
	[KernMember] public static int CompareTo(decimal self, decimal other) => self.CompareTo(other);
	[KernMember] public static string ToStr(decimal self) => self.ToString(CultureInfo.InvariantCulture);
	[KernMember] public static long ToI64(decimal self) => (long)self;
	[KernMember] public static double ToF64(decimal self) => (double)self;
}
