using System.Globalization;
using System.Numerics;
using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Numerics;

/// <summary>Точное десятичное число Kern-типа <c>decimal</c> (M6): обёртка <see cref="decimal"/>.</summary>
[KernType("decimal")]
public sealed class KernDecimal(decimal value)
{
	public decimal Value { get; } = value;

	public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Целое произвольной точности Kern-типа <c>bigint</c> (M6): обёртка <see cref="BigInteger"/>.</summary>
[KernType("bigint")]
public sealed class KernBigInt(BigInteger value)
{
	public BigInteger Value { get; } = value;

	public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Члены <c>decimal</c>: преобразования, округление и арифметика.</summary>
[KernType("decimal")]
public static class DecimalApi
{
	[KernName("to_str")]
	[KernMember]
	public static string ToStr(KNativeBox self) => Require(self).Value.ToString(CultureInfo.InvariantCulture);

	[KernName("to_f64")]
	[KernMember]
	public static double ToF64(KNativeBox self) => (double)Require(self).Value;

	[KernName("to_i64")]
	[KernMember]
	public static long ToI64(KNativeBox self) => (long)Require(self).Value;

	[KernMember]
	public static KernDecimal Abs(KNativeBox self) => new(decimal.Abs(Require(self).Value));

	[KernMember]
	public static KernDecimal Round(KNativeBox self, int digits) =>
		new(decimal.Round(Require(self).Value, digits, MidpointRounding.ToEven));

	[KernMember]
	public static KernDecimal Truncate(KNativeBox self) => new(decimal.Truncate(Require(self).Value));

	[KernMember(Operator = "+")]
	public static KernDecimal Add(KNativeBox self, KernDecimal other) => new(Require(self).Value + other.Value);

	[KernMember(Operator = "-")]
	public static KernDecimal Subtract(KNativeBox self, KernDecimal other) => new(Require(self).Value - other.Value);

	[KernMember(Operator = "*")]
	public static KernDecimal Multiply(KNativeBox self, KernDecimal other) => new(Require(self).Value * other.Value);

	[KernMember(Operator = "/")]
	public static KernDecimal Divide(KNativeBox self, KernDecimal other)
	{
		if (other.Value == 0)
			throw new KernPanic("decimal: деление на ноль");
		return new(Require(self).Value / other.Value);
	}

	[KernMember(Operator = "%")]
	public static KernDecimal Remainder(KNativeBox self, KernDecimal other)
	{
		if (other.Value == 0)
			throw new KernPanic("decimal: остаток от деления на ноль");
		return new(Require(self).Value % other.Value);
	}

	[KernMember(Operator = "-")]
	public static KernDecimal Negate(KNativeBox self) => new(-Require(self).Value);

	[KernMember(Operator = "==")]
	public static bool Equal(KNativeBox self, KernDecimal other) => Require(self).Value == other.Value;

	[KernMember(Operator = "!=")]
	public static bool NotEqual(KNativeBox self, KernDecimal other) => Require(self).Value != other.Value;

	[KernMember(Operator = "<")]
	public static bool Less(KNativeBox self, KernDecimal other) => Require(self).Value < other.Value;

	[KernMember(Operator = "<=")]
	public static bool LessOrEqual(KNativeBox self, KernDecimal other) => Require(self).Value <= other.Value;

	[KernMember(Operator = ">")]
	public static bool Greater(KNativeBox self, KernDecimal other) => Require(self).Value > other.Value;

	[KernMember(Operator = ">=")]
	public static bool GreaterOrEqual(KNativeBox self, KernDecimal other) => Require(self).Value >= other.Value;

	/// <summary>Сравнение (spec §16): отрицательное/ноль/положительное число.</summary>
	[KernMember]
	public static int Compare(KNativeBox self, KernDecimal other) => Require(self).Value.CompareTo(other.Value);

	private static KernDecimal Require(KNativeBox self) => self.Value switch
	{
		KernDecimal value => value,
		_ => throw new KernPanic($"ожидался decimal, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Члены <c>bigint</c>: преобразования, степень и арифметика.</summary>
[KernType("bigint")]
public static class BigIntApi
{
	[KernName("to_str")]
	[KernMember]
	public static string ToStr(KNativeBox self) => Require(self).Value.ToString(CultureInfo.InvariantCulture);

	[KernName("to_i64")]
	[KernMember]
	public static long ToI64(KNativeBox self) => (long)Require(self).Value;

	[KernName("to_f64")]
	[KernMember]
	public static double ToF64(KNativeBox self) => (double)Require(self).Value;

	[KernMember]
	public static KernBigInt Abs(KNativeBox self) => new(BigInteger.Abs(Require(self).Value));

	[KernMember]
	public static KernBigInt Pow(KNativeBox self, int exponent)
	{
		if (exponent < 0)
			throw new KernPanic("bigint.pow: отрицательная степень");
		return new(BigInteger.Pow(Require(self).Value, exponent));
	}

	[KernMember(Operator = "+")]
	public static KernBigInt Add(KNativeBox self, KernBigInt other) => new(Require(self).Value + other.Value);

	[KernMember(Operator = "-")]
	public static KernBigInt Subtract(KNativeBox self, KernBigInt other) => new(Require(self).Value - other.Value);

	[KernMember(Operator = "*")]
	public static KernBigInt Multiply(KNativeBox self, KernBigInt other) => new(Require(self).Value * other.Value);

	[KernMember(Operator = "/")]
	public static KernBigInt Divide(KNativeBox self, KernBigInt other)
	{
		if (other.Value.IsZero)
			throw new KernPanic("bigint: деление на ноль");
		return new(Require(self).Value / other.Value);
	}

	[KernMember(Operator = "%")]
	public static KernBigInt Remainder(KNativeBox self, KernBigInt other)
	{
		if (other.Value.IsZero)
			throw new KernPanic("bigint: остаток от деления на ноль");
		return new(Require(self).Value % other.Value);
	}

	[KernMember(Operator = "-")]
	public static KernBigInt Negate(KNativeBox self) => new(-Require(self).Value);

	[KernMember(Operator = "==")]
	public static bool Equal(KNativeBox self, KernBigInt other) => Require(self).Value == other.Value;

	[KernMember(Operator = "!=")]
	public static bool NotEqual(KNativeBox self, KernBigInt other) => Require(self).Value != other.Value;

	[KernMember(Operator = "<")]
	public static bool Less(KNativeBox self, KernBigInt other) => Require(self).Value < other.Value;

	[KernMember(Operator = "<=")]
	public static bool LessOrEqual(KNativeBox self, KernBigInt other) => Require(self).Value <= other.Value;

	[KernMember(Operator = ">")]
	public static bool Greater(KNativeBox self, KernBigInt other) => Require(self).Value > other.Value;

	[KernMember(Operator = ">=")]
	public static bool GreaterOrEqual(KNativeBox self, KernBigInt other) => Require(self).Value >= other.Value;

	[KernMember]
	public static int Compare(KNativeBox self, KernBigInt other) => Require(self).Value.CompareTo(other.Value);

	private static KernBigInt Require(KNativeBox self) => self.Value switch
	{
		KernBigInt value => value,
		_ => throw new KernPanic($"ожидался bigint, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Модуль <c>kern.numerics</c>: фабрики <c>decimal(...)</c> и <c>bigint(...)</c>.</summary>
[KernModule("kern.numerics")]
public static class NumericsModule
{
	/// <summary><c>decimal(text)</c>: разбор точного числа; ошибка — русская паника.</summary>
	[KernName("decimal")]
	[KernMember]
	public static KernDecimal Decimal(string text)
	{
		if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
			throw new KernPanic($"decimal: неверное число '{text}'");
		return new KernDecimal(value);
	}

	/// <summary><c>decimal(n)</c>: из целого.</summary>
	[KernName("decimal")]
	[KernMember]
	public static KernDecimal DecimalFromInt(long value) => new(value);

	/// <summary><c>decimal(x)</c>: из дробного.</summary>
	[KernName("decimal")]
	[KernMember]
	public static KernDecimal DecimalFromFloat(double value) => new((decimal)value);

	/// <summary><c>bigint(text)</c>: разбор целого произвольной точности.</summary>
	[KernName("bigint")]
	[KernMember]
	public static KernBigInt Big(string text)
	{
		if (!BigInteger.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger value))
			throw new KernPanic($"bigint: неверное число '{text}'");
		return new KernBigInt(value);
	}

	/// <summary><c>bigint(n)</c>: из целого.</summary>
	[KernName("bigint")]
	[KernMember]
	public static KernBigInt BigFromInt(long value) => new(value);
}
