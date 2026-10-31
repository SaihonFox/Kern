using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Binding.Wrappers;

/// <summary>Фантомные обёртки: T нужен только для подписи. В рантайме инстанцируются с T = KValue.</summary>
public readonly struct KList<T>(KListObject inner)
{
	public KListObject Inner { get; } = inner;
	public List<KValue> Items => Inner.Items;
	public int Count => Items.Count;
	public KValue this[int i] { get => Items[i]; set => Items[i] = value; }
	public static KList<T> Create(KType elementType) => new(new KListObject(elementType));
}

public readonly struct KArray<T>(KArrayObject inner)
{
	public KArrayObject Inner { get; } = inner;
	public KValue[] Items => Inner.Items;
}

public readonly struct KDict<TKey, TValue>(KDictObject inner)
{
	public KDictObject Inner { get; } = inner;
	public Dictionary<KValue, KValue> Items => Inner.Items;
}

/// <summary>task&lt;T&gt;: задача VM (KTask), передаваемая в нативный код и возвращаемая из него без обёртки в .NET Task.</summary>
public readonly struct KTaskRef<T>(KTask inner) { public KTask Inner { get; } = inner; }

/// <summary>task&lt;T?&gt;: то же, но результат может быть nil.</summary>
public readonly struct KTaskNullable<T>(KTask inner) { public KTask Inner { get; } = inner; }

public readonly struct KTuple<T1, T2>(KTupleObject inner) { public KTupleObject Inner { get; } = inner; }
public readonly struct KTuple<T1, T2, T3>(KTupleObject inner) { public KTupleObject Inner { get; } = inner; }

public readonly struct KResult<T, E>(KResultObject inner)
{
	public KResultObject Inner { get; } = inner;
	public static KResult<T, E> Ok(KValue v) => new(new KResultObject(true, v));
	public static KResult<T, E> Err(KValue v) => new(new KResultObject(false, v));
}

/// <summary>Kern-функция fun(A)->R, вызываемая из нативного кода.</summary>
public readonly struct KFunc<R>(KFunc.Callable inner) { public KFunc.Callable Inner { get; } = inner; public KValue Invoke() => Inner.Invoke([]); }
public readonly struct KFunc<A, R>(KFunc.Callable inner) { public KFunc.Callable Inner { get; } = inner; public KValue Invoke(KValue a) => Inner.Invoke([a]); }
public readonly struct KFunc<A, B, R>(KFunc.Callable inner) { public KFunc.Callable Inner { get; } = inner; public KValue Invoke(KValue a, KValue b) => Inner.Invoke([a, b]); }
public readonly struct KFunc<A, B, C, R>(KFunc.Callable inner) { public KFunc.Callable Inner { get; } = inner; public KValue Invoke(KValue a, KValue b, KValue c) => Inner.Invoke([a, b, c]); }
public readonly struct KAction(KFunc.Callable inner) { public KFunc.Callable Inner { get; } = inner; public void Invoke() => Inner.Invoke([]); }
public readonly struct KAction<A>(KFunc.Callable inner) { public KFunc.Callable Inner { get; } = inner; public void Invoke(KValue a) => Inner.Invoke([a]); }
public readonly struct KAction<A, B>(KFunc.Callable inner) { public KFunc.Callable Inner { get; } = inner; public void Invoke(KValue a, KValue b) => Inner.Invoke([a, b]); }

public static class KFunc
{
	/// <summary>Обёртка над значением-функцией VM. Invoke синхронно исполняет Kern-код в VM.</summary>
	public sealed class Callable(Interpreter vm, KValue function)
	{
		public Interpreter Vm { get; } = vm;
		public KValue Function { get; } = function;
		public KValue Invoke(ReadOnlySpan<KValue> args) => Vm.CallFunction(Function, args);
	}
}
