using System.Runtime.CompilerServices;
using Kern.Binding.Attributes;
using Kern.Vm.Values;

namespace Kern.Runtime.Numerics;

/// <summary>
/// Универсальные числовые члены (все ширины i8..u64, f16..f64). В рантайме T = KValue, поэтому значения
/// переинтерпретируются без бокса (<see cref="NumCast"/>). Ограничения Kern задаются [KernConstraint], не `where`.
/// </summary>
[KernType("num")]
public static class NumApi
{
	private const KernConstraintKind N = KernConstraintKind.Num;

	[KernProperty] public static T Abs<[KernConstraint(N)] T>(T self)
	{
		var v = NumCast.In(self);
		if (v.IsFloat) return NumCast.Out<T>(Num.FromDouble(v.Kind, System.Math.Abs(v.AsF64)));
		if (v.IsUnsigned) return self;
		return NumCast.Out<T>(Num.Wrap(v.Kind, v.Bits < 0 ? unchecked(-v.Bits) : v.Bits));
	}

	[KernProperty] public static int Sign<[KernConstraint(N)] T>(T self)
	{
		var v = NumCast.In(self);
		if (v.IsFloat) return double.IsNaN(v.AsF64) ? 0 : System.Math.Sign(v.AsF64);
		return v.IsUnsigned ? (v.Bits == 0 ? 0 : 1) : System.Math.Sign(v.Bits);
	}

	[KernProperty] public static bool IsZero<[KernConstraint(N)] T>(T self) { var v = NumCast.In(self); return v.IsFloat ? v.AsF64 == 0 : v.Bits == 0; }
	[KernProperty] public static bool IsPositive<[KernConstraint(N)] T>(T self) => Sign(self) > 0;
	[KernProperty] public static bool IsNegative<[KernConstraint(N)] T>(T self) => Sign(self) < 0;

	[KernMember] public static string ToStr<[KernConstraint(N)] T>(T self) => Num.ToStr(NumCast.In(self));

	[KernMember] public static T Min<[KernConstraint(N)] T>(T self, T other) => Num.Compare(NumCast.In(other), NumCast.In(self)) < 0 ? other : self;
	[KernMember] public static T Max<[KernConstraint(N)] T>(T self, T other) => Num.Compare(NumCast.In(other), NumCast.In(self)) > 0 ? other : self;
	[KernMember] public static T Clamp<[KernConstraint(N)] T>(T self, T low, T high) =>
		Num.Compare(NumCast.In(self), NumCast.In(low)) < 0 ? low : Num.Compare(NumCast.In(self), NumCast.In(high)) > 0 ? high : self;
	[KernMember] public static bool Between<[KernConstraint(N)] T>(T self, T low, T high) =>
		Num.Compare(NumCast.In(self), NumCast.In(low)) >= 0 && Num.Compare(NumCast.In(self), NumCast.In(high)) <= 0;

	// ---------- преобразования между ширинами (wrap для целых, усечение float→int) ----------
	[KernMember] public static sbyte ToI8<[KernConstraint(N)] T>(T self) => (sbyte)Num.Convert(NumCast.In(self), ValueKind.I8).Bits;
	[KernMember] public static short ToI16<[KernConstraint(N)] T>(T self) => (short)Num.Convert(NumCast.In(self), ValueKind.I16).Bits;
	[KernMember] public static int ToI32<[KernConstraint(N)] T>(T self) => (int)Num.Convert(NumCast.In(self), ValueKind.I32).Bits;
	[KernMember] public static long ToI64<[KernConstraint(N)] T>(T self) => Num.Convert(NumCast.In(self), ValueKind.I64).Bits;
	[KernMember] public static byte ToU8<[KernConstraint(N)] T>(T self) => (byte)Num.Convert(NumCast.In(self), ValueKind.U8).Bits;
	[KernMember] public static ushort ToU16<[KernConstraint(N)] T>(T self) => (ushort)Num.Convert(NumCast.In(self), ValueKind.U16).Bits;
	[KernMember] public static uint ToU32<[KernConstraint(N)] T>(T self) => (uint)Num.Convert(NumCast.In(self), ValueKind.U32).Bits;
	[KernMember] public static ulong ToU64<[KernConstraint(N)] T>(T self) => Num.Convert(NumCast.In(self), ValueKind.U64).AsU64;
	[KernMember] public static Half ToF16<[KernConstraint(N)] T>(T self) => (Half)NumCast.In(self).AsF64;
	[KernMember] public static float ToF32<[KernConstraint(N)] T>(T self) => (float)NumCast.In(self).AsF64;
	[KernMember] public static double ToF64<[KernConstraint(N)] T>(T self) => NumCast.In(self).AsF64;
	[KernMember] public static decimal ToDecimal<[KernConstraint(N)] T>(T self)
	{
		var v = NumCast.In(self);
		return v.IsFloat ? (decimal)v.AsF64 : v.IsUnsigned ? (decimal)v.AsU64 : v.Bits;
	}
	[KernMember] public static char ToChar<[KernConstraint(N)] T>(T self) => (char)NumCast.In(self).Bits;

	/// <summary>Сравнение: -1/0/1 (NaN считается равным).</summary>
	[KernMember] public static int CompareTo<[KernConstraint(N)] T>(T self, T other) { int c = Num.Compare(NumCast.In(self), NumCast.In(other)); return c == 2 ? 0 : c; }
}

/// <summary>Переинтерпретация T ↔ KValue: в рантайме числовые generic'и всегда инстанцируются с T = KValue.</summary>
internal static class NumCast
{
	public static KValue In<T>(T v) => Unsafe.As<T, KValue>(ref v);
	public static T Out<T>(KValue v) => Unsafe.As<KValue, T>(ref v);
}
