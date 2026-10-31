using Kern.Binding.Attributes;
using Kern.Vm.Values;

namespace Kern.Runtime.Numerics;

/// <summary>Члены вещественных типов f16, f32, f64. Результат сохраняет ширину исходного типа.</summary>
[KernType("fnum")]
public static class FloatApi
{
	private const KernConstraintKind F = KernConstraintKind.Fnum;

	private static T Map<T>(T self, Func<double, double> f)
	{
		var v = NumCast.In(self);
		return NumCast.Out<T>(Num.FromDouble(v.Kind, f(v.AsF64)));
	}

	[KernProperty] public static T Floor<[KernConstraint(F)] T>(T self) => Map(self, System.Math.Floor);
	[KernProperty] public static T Ceil<[KernConstraint(F)] T>(T self) => Map(self, System.Math.Ceiling);
	[KernProperty] public static T Round<[KernConstraint(F)] T>(T self) => Map(self, d => System.Math.Round(d, MidpointRounding.AwayFromZero));
	[KernProperty] public static T Truncate<[KernConstraint(F)] T>(T self) => Map(self, System.Math.Truncate);
	[KernProperty] public static T Fract<[KernConstraint(F)] T>(T self) => Map(self, d => d - System.Math.Truncate(d));
	[KernProperty] public static T Sqrt<[KernConstraint(F)] T>(T self) => Map(self, System.Math.Sqrt);

	[KernProperty] public static bool IsNan<[KernConstraint(F)] T>(T self) => double.IsNaN(NumCast.In(self).AsF64);
	[KernProperty] public static bool IsInfinite<[KernConstraint(F)] T>(T self) => double.IsInfinity(NumCast.In(self).AsF64);
	[KernProperty] public static bool IsFinite<[KernConstraint(F)] T>(T self) => double.IsFinite(NumCast.In(self).AsF64);

	[KernMember] public static T RoundTo<[KernConstraint(F)] T>(T self, int digits) =>
		Map(self, d => System.Math.Round(d, System.Math.Clamp(digits, 0, 15), MidpointRounding.AwayFromZero));

	[KernMember] public static T Pow<[KernConstraint(F)] T>(T self, T exp) => Map(self, d => System.Math.Pow(d, NumCast.In(exp).AsF64));

	/// <summary>Приближённое равенство с допуском eps.</summary>
	[KernMember] public static bool Approx<[KernConstraint(F)] T>(T self, T other, double eps = 1e-9) =>
		System.Math.Abs(NumCast.In(self).AsF64 - NumCast.In(other).AsF64) <= eps;

	/// <summary>Битовое представление числа (f64 → u64).</summary>
	[KernProperty] public static ulong Bits<[KernConstraint(F)] T>(T self) => BitConverter.DoubleToUInt64Bits(NumCast.In(self).AsF64);
}
