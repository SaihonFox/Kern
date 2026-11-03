using System.Globalization;
using Kern.Binding.Attributes;
using Kern.Binding.Formatting;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Test;

/// <summary>
/// Модуль <c>kern.test</c> (spec §15/§18c): утверждения для <c>#[test]</c>-функций.
/// Неудача — <see cref="KernPanic"/> с русским сообщением; значения печатаются через
/// <see cref="KernFormat"/>. Равенство — по правилам оператора <c>==</c>
/// (<see cref="KValueMath.AreEqual"/>: числа сравниваются между видами, unit — структурно,
/// прочие ссылки — по идентичности).
/// </summary>
[KernModule("kern.test")]
public static class TestModule
{
	private const string DefaultAssertMessage = "утверждение не выполнено";

	/// <summary><c>assert(condition)</c>: паника, если условие ложно.</summary>
	[KernName("assert")]
	[KernMember]
	public static void Assert(bool condition) => Assert(condition, DefaultAssertMessage);

	/// <summary><c>assert(condition, message)</c>: паника с текстом <paramref name="message"/>.</summary>
	[KernName("assert")]
	[KernMember]
	public static void Assert(bool condition, string message)
	{
		if (!condition)
			throw new KernPanic(message);
	}

	/// <summary><c>assert_eq(expected, actual)</c>: значения должны быть равны по <c>==</c>.</summary>
	[KernName("assert_eq")]
	[KernMember]
	public static void AssertEq(KValue expected, KValue actual)
	{
		if (!KValueMath.AreEqual(expected, actual))
		{
			throw new KernPanic(
				$"assert_eq не выполнено: ожидалось {KernFormat.Value(expected)}, получено {KernFormat.Value(actual)}");
		}
	}

	/// <summary><c>assert_ne(expected, actual)</c>: значения не должны быть равны по <c>==</c>.</summary>
	[KernName("assert_ne")]
	[KernMember]
	public static void AssertNe(KValue expected, KValue actual)
	{
		if (KValueMath.AreEqual(expected, actual))
			throw new KernPanic(
				$"assert_ne не выполнено: ожидалось различие, оба значения {KernFormat.Value(expected)} и {KernFormat.Value(actual)}");
	}

	/// <summary><c>assert_near(expected, actual)</c> с допуском <c>1e-9</c>.</summary>
	[KernName("assert_near")]
	[KernMember]
	public static void AssertNear(double expected, double actual) => AssertNear(expected, actual, 1e-9);

	/// <summary><c>assert_near(expected, actual, epsilon)</c>: |expected − actual| ≤ epsilon.</summary>
	[KernName("assert_near")]
	[KernMember]
	public static void AssertNear(double expected, double actual, double epsilon)
	{
		if (double.IsNaN(epsilon) || epsilon < 0)
			throw new KernPanic($"assert_near: epsilon должен быть неотрицательным, получено {Number(epsilon)}");

		bool close = expected == actual || System.Math.Abs(expected - actual) <= epsilon;
		if (!close)
		{
			throw new KernPanic(
				$"assert_near не выполнено: ожидалось {Number(expected)} ± {Number(epsilon)}, получено {Number(actual)}");
		}
	}

	/// <summary><c>assert_nil(value)</c>: значение должно быть <c>nil</c> (или <c>none</c>).</summary>
	[KernName("assert_nil")]
	[KernMember]
	public static void AssertNil(KValue value)
	{
		if (!value.IsNilOrNone)
			throw new KernPanic($"assert_nil не выполнено: получено {KernFormat.Value(value)}");
	}

	/// <summary><c>assert_nil(nil)</c>: отдельная перегрузка для литерала/значения <c>nil</c> (<c>object?</c>).</summary>
	[KernName("assert_nil")]
	[KernMember]
	public static void AssertNilNullable(KValue? value)
	{
		if (value is { } actual && !actual.IsNilOrNone)
			throw new KernPanic($"assert_nil не выполнено: получено {KernFormat.Value(actual)}");
	}

	/// <summary><c>assert_not_nil(value)</c>: значение не должно быть <c>nil</c>.</summary>
	[KernName("assert_not_nil")]
	[KernMember]
	public static void AssertNotNull(KValue value)
	{
		if (value.IsNilOrNone)
			throw new KernPanic("assert_not_nil не выполнено: значение nil");
	}

	/// <summary><c>assert_not_nil(nil)</c>: та же проверка для <c>object?</c> (литерал <c>nil</c>).</summary>
	[KernName("assert_not_nil")]
	[KernMember]
	public static void AssertNotNullNullable(KValue? value)
	{
		if (value is not { } actual || actual.IsNilOrNone)
			throw new KernPanic("assert_not_nil не выполнено: значение nil");
	}

	/// <summary><c>fail(message)</c>: безусловная паника (по смыслу — <c>never</c>).</summary>
	[KernName("fail")]
	[KernMember]
	[KernReturn("never")]
	public static void Fail(string message) => throw new KernPanic(message);

	private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
