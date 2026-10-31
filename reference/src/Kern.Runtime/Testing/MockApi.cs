using Kern.Binding.Attributes;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Testing;

/// <summary>
/// Моки: заглушка абстрактного типа (<c>@type abstract</c>) без написания класса вручную.
/// <code>
/// use kern.test[mock, assert_eq]
/// Mailer m = mock.create<Mailer>()
/// mock.returns(m, "send", true)                       // на любые аргументы
/// mock.returns_for(m, "send", ["a@b.c", "hi"], false) // на конкретные аргументы (приоритетнее)
/// mock.fail(m, "ping", "сервер недоступен")           // метод паникует
/// mock.handle(m, "ping", (args) -> args.len)          // свой обработчик
/// m.send("a@b.c", "hi")
/// mock.verify(m, "send", 1)                           // ровно 1 вызов
/// mock.verify_with(m, "send", ["a@b.c", "hi"], 1)
/// </code>
/// Метод без настройки возвращает nil/0/false/"" (значение по умолчанию, не panic). Вызовы записываются всегда.
/// Мок создаётся только для абстрактного типа/открытого типа с абстрактными методами; реализованные (не абстрактные) методы работают как обычно.
/// </summary>
[KernType("mock")]
public static class MockApi
{
	private sealed record Rule(string Method, KValue[]? Args, KValue Result, string? Error, KValue Handler);
	private sealed record Call(string Method, KValue[] Args);

	private sealed class MockState
	{
		public readonly List<Rule> Rules = [];
		public readonly List<Call> Calls = [];
	}

	[KernStatic] public static T Create<T>([KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs)
	{
		var type = Kv.Last(typeArgs) as KClassType ?? throw new KernPanic("mock.create: T должен быть типом (type/abstract), не примитивом");
		var state = new MockState();
		var obj = new KObject(type, new KValue[vm.Info.Classes[type.DefIndex].FieldCount])
		{
			Native = new KProxyState((v, fi, args) => Dispatch(v, state, fi, args)) { Tag = state },
		};
		return Kv.As<T>(KValue.Obj(obj));
	}

	private static MockState State(KValue mock) =>
		mock.Ref is KObject { Native: KProxyState { Tag: MockState s } } ? s : throw new KernPanic("значение не является моком (создайте через mock.create<T>())");

	private static KValue Dispatch(Interpreter vm, MockState st, int fi, KValue[] args)
	{
		string name = vm.Module.Functions[fi].Name;
		st.Calls.Add(new Call(name, args));
		// приоритет: правило с конкретными аргументами, затем общее; позднее правило перекрывает раннее
		Rule? rule = null;
		for (int i = st.Rules.Count - 1; i >= 0 && rule is null; i--)
			if (st.Rules[i].Method == name && st.Rules[i].Args is { } a && ArgsEq(a, args)) rule = st.Rules[i];
		for (int i = st.Rules.Count - 1; i >= 0 && rule is null; i--)
			if (st.Rules[i].Method == name && st.Rules[i].Args is null) rule = st.Rules[i];
		if (rule is null) return default;
		if (rule.Error is not null) throw new KernPanic(rule.Error);
		if (rule.Handler.Ref is not null) return vm.CallFunction(rule.Handler, [KValue.Obj(new KListObject(TypeFactory.Object, [.. args]))]);   // обработчик получает список аргументов
		return rule.Result;
	}

	private static bool ArgsEq(KValue[] a, KValue[] b)
	{
		if (a.Length != b.Length) return false;
		for (int i = 0; i < a.Length; i++) if (!Kv.Eq(a[i], b[i])) return false;
		return true;
	}

	private static KValue[] ListItems(KValue v) =>
		v.Ref is KListObject l ? [.. l.Items] : throw new KernPanic("ожидался список аргументов");

	[KernStatic] public static void Returns(KValue mock, string method, KValue result) =>
		State(mock).Rules.Add(new Rule(method, null, result, null, default));

	[KernStatic] public static void ReturnsFor(KValue mock, string method, KValue args, KValue result) =>
		State(mock).Rules.Add(new Rule(method, ListItems(args), result, null, default));

	[KernStatic] public static void Fail(KValue mock, string method, string message) =>
		State(mock).Rules.Add(new Rule(method, null, default, message, default));

	[KernStatic] public static void Handle(KValue mock, string method, KValue handler) =>
		State(mock).Rules.Add(new Rule(method, null, default, null, handler));

	/// <summary>Число вызовов метода (по имени) на всех аргументах.</summary>
	[KernStatic] public static int Calls(KValue mock, string method) => State(mock).Calls.Count(c => c.Method == method);

	[KernStatic] public static int CallsWith(KValue mock, string method, KValue args)
	{
		var want = ListItems(args);
		return State(mock).Calls.Count(c => c.Method == method && ArgsEq(want, c.Args));
	}

	[KernStatic] public static void Verify(KValue mock, string method, int times)
	{
		int n = Calls(mock, method);
		if (n != times) throw new KernPanic($"mock.verify: {method} вызван {n} раз, ожидалось {times}");
	}

	[KernStatic] public static void VerifyWith([KernVm] Interpreter vm, KValue mock, string method, KValue args, int times)
	{
		int n = CallsWith(mock, method, args);
		if (n != times) throw new KernPanic($"mock.verify_with: {method}({string.Join(", ", ListItems(args).Select(vm.Stringify))}) вызван {n} раз, ожидалось {times}");
	}

	[KernStatic] public static void VerifyNever(KValue mock, string method) => Verify(mock, method, 0);

	/// <summary>Сбросить журнал вызовов (правила остаются).</summary>
	[KernStatic] public static void ResetCalls(KValue mock) => State(mock).Calls.Clear();
}
