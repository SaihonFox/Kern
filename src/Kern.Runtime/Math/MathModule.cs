using Kern.Binding.Attributes;

namespace Kern.Runtime.Math;

/// <summary>
/// Модуль <c>kern.math</c> (spec §19). Внутри пространства имён
/// <c>Kern.Runtime.Math</c> системная математика вызывается только как
/// <c>System.Math.X</c>.
/// </summary>
[KernModule("kern.math")]
public static class MathModule
{
	[KernMember]
	public static int Abs(int value) => System.Math.Abs(value);

	[KernMember]
	public static long Abs(long value) => System.Math.Abs(value);

	[KernMember]
	public static double Abs(double value) => System.Math.Abs(value);

	[KernMember]
	public static int Min(int left, int right) => System.Math.Min(left, right);

	[KernMember]
	public static long Min(long left, long right) => System.Math.Min(left, right);

	[KernMember]
	public static double Min(double left, double right) => System.Math.Min(left, right);

	[KernMember]
	public static int Max(int left, int right) => System.Math.Max(left, right);

	[KernMember]
	public static long Max(long left, long right) => System.Math.Max(left, right);

	[KernMember]
	public static double Max(double left, double right) => System.Math.Max(left, right);

	[KernMember]
	public static int Clamp(int value, int minimum, int maximum) =>
		System.Math.Clamp(value, minimum, maximum);

	[KernMember]
	public static double Clamp(double value, double minimum, double maximum) =>
		System.Math.Clamp(value, minimum, maximum);

	[KernMember]
	public static int Sign(int value) => System.Math.Sign(value);

	[KernMember]
	public static double Sign(double value) => System.Math.Sign(value);

	[KernMember]
	public static double Sqrt(double value) => System.Math.Sqrt(value);

	[KernMember]
	public static double Cbrt(double value) => System.Math.Cbrt(value);

	[KernMember]
	public static double Pow(double value, double exponent) => System.Math.Pow(value, exponent);

	[KernMember]
	public static double Exp(double value) => System.Math.Exp(value);

	[KernMember]
	public static double Log(double value) => System.Math.Log(value);

	[KernMember]
	public static double Log2(double value) => System.Math.Log2(value);

	[KernMember]
	public static double Log10(double value) => System.Math.Log10(value);

	[KernMember]
	public static double Sin(double value) => System.Math.Sin(value);

	[KernMember]
	public static double Cos(double value) => System.Math.Cos(value);

	[KernMember]
	public static double Tan(double value) => System.Math.Tan(value);

	[KernMember]
	public static double Asin(double value) => System.Math.Asin(value);

	[KernMember]
	public static double Acos(double value) => System.Math.Acos(value);

	[KernMember]
	public static double Atan(double value) => System.Math.Atan(value);

	[KernMember]
	public static double Atan2(double y, double x) => System.Math.Atan2(y, x);

	[KernMember]
	public static double Floor(double value) => System.Math.Floor(value);

	[KernMember]
	public static double Ceil(double value) => System.Math.Ceiling(value);

	[KernMember]
	public static double Round(double value) => System.Math.Round(value, MidpointRounding.ToEven);

	[KernMember]
	public static double Round(double value, int digits) =>
		System.Math.Round(value, digits, MidpointRounding.ToEven);

	[KernMember]
	public static double Truncate(double value) => System.Math.Truncate(value);

	[KernMember]
	public static double Hypot(double left, double right) => System.Math.Sqrt(left * left + right * right);

	[KernMember]
	public static double Pi() => System.Math.PI;

	[KernMember]
	public static double E() => System.Math.E;

	[KernName("is_nan")]
	[KernMember]
	public static bool IsNaN(double value) => double.IsNaN(value);

	[KernName("is_inf")]
	[KernMember]
	public static bool IsInf(double value) => double.IsInfinity(value);

	[KernName("to_i32")]
	[KernMember]
	public static int ToI32(double value) => checked((int)value);

	[KernName("to_f64")]
	[KernMember]
	public static double ToF64(int value) => value;
}
