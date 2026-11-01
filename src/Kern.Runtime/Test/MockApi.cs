using Kern.Binding.Attributes;
using Kern.Bytecode;
using Kern.Vm;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Test;

/// <summary>
/// Состояние мока (spec round 9): правила по методам, журнал вызовов и обработчик
/// абстрактных методов. Объект абстрактного типа создаётся без конструктора; VM
/// направляет вызовы <c>@fun</c> в <see cref="IKProxyHandler"/>.
/// </summary>
public sealed class MockProxy : IKProxyHandler
{
	private sealed record Rule(KValue[]? Arguments, string Kind, KValue Value, string? Error);

	private sealed class MethodState
	{
		public List<Rule> Rules { get; } = [];
		public List<KValue[]> Calls { get; } = [];
	}

	private readonly object _gate = new();
	private readonly Dictionary<string, MethodState> _methods = new(StringComparer.Ordinal);

	public KValue Invoke(Interpreter interpreter, KObject receiver, FunctionDef method, KValue[] arguments)
	{
		KValue[] callArguments = arguments.Length > 0 ? arguments[1..] : [];
		Rule? selected;
		lock (_gate)
		{
			if (!_methods.TryGetValue(method.Name, out MethodState? state))
				_methods[method.Name] = state = new MethodState();
			state.Calls.Add(callArguments);
			selected = state.Rules.Count == 0 ? null : Select(state.Rules, callArguments);
		}

		if (selected is null)
			return DefaultValue(method.ReturnType);

		return selected.Kind switch
		{
			"panic" => throw new KernPanic(selected.Error ?? "мок: метод упал"),
			"function" => interpreter.RunValue(selected.Value, ArgumentList(interpreter, callArguments)),
			_ => selected.Value,
		};
	}

	public void Returns(string method, KValue value) =>
		Add(method, new Rule(null, "value", value, null));

	public void ReturnsFor(string method, KValue[] arguments, KValue value) =>
		Add(method, new Rule(arguments, "value", value, null));

	public void Fail(string method, string message) =>
		Add(method, new Rule(null, "panic", KValue.None, message));

	public void Handle(string method, KValue function) =>
		Add(method, new Rule(null, "function", function, null));

	public int Calls(string method) => Count(method, null);

	public int CallsWith(string method, KValue[] arguments) => Count(method, arguments);

	public void ResetCalls()
	{
		lock (_gate)
			foreach (MethodState state in _methods.Values)
				state.Calls.Clear();
	}

	private void Add(string method, Rule rule)
	{
		lock (_gate)
		{
			if (!_methods.TryGetValue(method, out MethodState? state))
				_methods[method] = state = new MethodState();
			state.Rules.Add(rule);
		}
	}

	private int Count(string method, KValue[]? arguments)
	{
		lock (_gate)
		{
			if (!_methods.TryGetValue(method, out MethodState? state))
				return 0;
			return arguments is null
				? state.Calls.Count
				: state.Calls.Count(call => call.Length == arguments.Length && Matches(call, arguments));
		}
	}

	private static Rule? Select(List<Rule> rules, KValue[] arguments)
	{
		for (int index = rules.Count - 1; index >= 0; index--)
		{
			Rule rule = rules[index];
			if (rule.Arguments is null || Matches(arguments, rule.Arguments))
				return rule;
		}

		return null;
	}

	private static bool Matches(KValue[] left, KValue[] right)
	{
		if (left.Length != right.Length)
			return false;
		for (int index = 0; index < left.Length; index++)
			if (!left[index].Equals(right[index]))
				return false;
		return true;
	}

	private static KValue ArgumentList(Interpreter interpreter, KValue[] arguments) =>
		KValue.Obj(new KList(interpreter.Types.Prim(PrimKind.Object), arguments));

	/// <summary>Значение метода без настройки — по умолчанию (spec round 9), не паника.</summary>
	internal static KValue DefaultValue(TypeSig type) => type switch
	{
		TypeSig.PrimSig { Kind: PrimKind.Void } => KValue.Void,
		TypeSig.PrimSig { Kind: PrimKind.Bool } => KValue.Bool(false),
		TypeSig.PrimSig { Kind: PrimKind.Char } => KValue.Char('\0'),
		TypeSig.PrimSig { Kind: PrimKind.Str } => KValue.Str(""),
		TypeSig.PrimSig { Kind: PrimKind.I8 } => KValue.I8(0),
		TypeSig.PrimSig { Kind: PrimKind.I16 } => KValue.I16(0),
		TypeSig.PrimSig { Kind: PrimKind.I32 } => KValue.I32(0),
		TypeSig.PrimSig { Kind: PrimKind.I64 } => KValue.I64(0),
		TypeSig.PrimSig { Kind: PrimKind.U8 } => KValue.U8(0),
		TypeSig.PrimSig { Kind: PrimKind.U16 } => KValue.U16(0),
		TypeSig.PrimSig { Kind: PrimKind.U32 } => KValue.U32(0),
		TypeSig.PrimSig { Kind: PrimKind.U64 } => KValue.U64(0),
		TypeSig.PrimSig { Kind: PrimKind.F16 } => KValue.F16(Half.Zero),
		TypeSig.PrimSig { Kind: PrimKind.F32 } => KValue.F32(0),
		TypeSig.PrimSig { Kind: PrimKind.F64 } => KValue.F64(0),
		_ => KValue.Nil,
	};
}

/// <summary><c>kern.test</c>, моки (spec round 9): <c>mock.create/returns/returns_for/fail/handle</c>
/// и проверки вызовов.</summary>
[KernType("mock")]
public static class MockApi
{
	/// <summary><c>mock.create&lt;T&gt;()</c>: заглушка абстрактного <c>@type</c>.</summary>
	[KernStatic]
	[KernReturn("T")]
	public static KValue Create<T>(Interpreter interpreter, [KernTypeArgs] KType[] typeArguments)
	{
		if (typeArguments.Length != 1 || typeArguments[0] is not KClassType @class)
			throw new KernPanic("mock.create: нужен один аргумент типа — абстрактный @type");
		if (!@class.Definition.Kind.Equals(TypeDefKind.Class))
			throw new KernPanic($"mock.create: '{@class.Canonical}' не является типом");

		KValue[] fields = new KValue[@class.Definition.Fields.Count];
		Array.Fill(fields, KValue.None);
		var target = new KObject(@class, fields) { Native = new MockProxy() };
		return KValue.Obj(target);
	}

	[KernStatic]
	public static void Returns(KValue target, string method, KValue value) => Require(target).Returns(method, value);

	[KernStatic]
	[KernName("returns_for")]
	public static void ReturnsFor(KValue target, string method, KValue arguments, KValue value) =>
		Require(target).ReturnsFor(method, ArgumentValues(arguments), value);

	[KernStatic]
	public static void Fail(KValue target, string method, string message) => Require(target).Fail(method, message);

	[KernStatic]
	public static void Handle(Interpreter interpreter, KValue target, string method, KValue function) =>
		Require(target).Handle(method, function);

	[KernStatic]
	public static int Calls(KValue target, string method) => Require(target).Calls(method);

	[KernStatic]
	[KernName("calls_with")]
	public static int CallsWith(KValue target, string method, KValue arguments) =>
		Require(target).CallsWith(method, ArgumentValues(arguments));

	[KernStatic]
	public static void Verify(KValue target, string method, int count)
	{
		int actual = Require(target).Calls(method);
		if (actual != count)
			throw new KernPanic($"mock.verify: '{method}' вызван {actual} раз, ожидалось {count}");
	}

	[KernStatic]
	[KernName("verify_with")]
	public static void VerifyWith(KValue target, string method, KValue arguments, int count)
	{
		int actual = Require(target).CallsWith(method, ArgumentValues(arguments));
		if (actual != count)
			throw new KernPanic($"mock.verify_with: '{method}' с такими аргументами вызван {actual} раз, ожидалось {count}");
	}

	[KernStatic]
	[KernName("verify_never")]
	public static void VerifyNever(KValue target, string method)
	{
		int actual = Require(target).Calls(method);
		if (actual != 0)
			throw new KernPanic($"mock.verify_never: '{method}' вызывался {actual} раз");
	}

	[KernStatic]
	[KernName("reset_calls")]
	public static void ResetCalls(KValue target) => Require(target).ResetCalls();

	private static MockProxy Require(KValue target) =>
		target.Ref is KObject { Native: MockProxy proxy }
			? proxy
			: throw new KernPanic("mock: ожидался объект, созданный mock.create<...>()");

	private static KValue[] ArgumentValues(KValue arguments) =>
		arguments.Ref is KList list
			? [.. list.Items]
			: throw new KernPanic($"mock: ожидался список аргументов, получено {arguments.TypeName}");
}
