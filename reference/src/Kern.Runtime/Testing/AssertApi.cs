using Kern.Binding.Attributes;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Testing;

/// <summary>Проверки для тестов (<c>use kern.test[assert_eq]</c>). Нарушение = panic с понятным сообщением; <c>kernc test</c> считает такой тест проваленным.</summary>
[KernModule("kern.test")]
public static class AssertApi
{
	private static string Show(Interpreter vm, KValue v) => v.Kind == ValueKind.Str ? "\"" + v.AsStr + "\"" : vm.Stringify(v);

	[KernMember] public static void Assert(bool condition, string message = "условие не выполнено")
	{
		if (!condition) throw new KernPanic("assert: " + message);
	}

	[KernMember] public static void AssertEq([KernVm] Interpreter vm, KValue expected, KValue actual, string? message = null)
	{
		if (!Kv.Eq(expected, actual))
			throw new KernPanic($"assert_eq: ожидалось {Show(vm, expected)}, получено {Show(vm, actual)}" + (message is null ? "" : $" ({message})"));
	}

	[KernMember] public static void AssertNe([KernVm] Interpreter vm, KValue unexpected, KValue actual, string? message = null)
	{
		if (Kv.Eq(unexpected, actual))
			throw new KernPanic($"assert_ne: значение не должно быть {Show(vm, actual)}" + (message is null ? "" : $" ({message})"));
	}

	[KernMember] public static void AssertNil(KValue actual)
	{
		if (!actual.IsNilOrNone) throw new KernPanic("assert_nil: значение не nil");
	}

	[KernMember] public static void AssertNotNil(KValue actual)
	{
		if (actual.IsNilOrNone) throw new KernPanic("assert_not_nil: значение nil");
	}

	/// <summary>Сравнение чисел с допуском: <c>assert_near(0.3, 0.1 + 0.2, 1e-9)</c>.</summary>
	[KernMember] public static void AssertNear(double expected, double actual, double epsilon = 1e-9)
	{
		if (System.Math.Abs(expected - actual) > epsilon)
			throw new KernPanic($"assert_near: ожидалось {expected}±{epsilon}, получено {actual}");
	}

	[KernMember] public static void Fail(string message = "тест провален") => throw new KernPanic("fail: " + message);
}
