using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Binding.Values;

/// <summary>Нетипизированная функция-значение Kern: вызов идёт через интерпретатор.</summary>
public readonly struct KFunc
{
	public KFunc(KValue value)
	{
		if (value.Ref is not (KClosure or Kern.Bytecode.FunctionDef))
			throw new KernPanic($"KFunc: ожидалась функция, получено {value.TypeName}");
		Value = value;
	}

	public KValue Value { get; }

	public KValue Invoke(Interpreter interpreter, params KValue[] arguments) =>
		interpreter.RunValue(Value, arguments);

	public static implicit operator KValue(KFunc function) => function.Value;

	public override string ToString() => Value.ToString();
}

/// <summary>Функция `fun(..) -&gt; R`.</summary>
public readonly struct KFunc<TResult>
{
	public KFunc(KValue value)
	{
		Raw = new KFunc(value);
	}

	public KFunc Raw { get; }

	public KValue Value => Raw.Value;

	public TResult Invoke(Interpreter interpreter) =>
		Marshalling.KMarshal.To<TResult>(Raw.Invoke(interpreter));

	public static implicit operator KValue(KFunc<TResult> function) => function.Value;
}

/// <summary>Функция `fun(A) -&gt; R`.</summary>
public readonly struct KFunc<T, TResult>
{
	public KFunc(KValue value)
	{
		Raw = new KFunc(value);
	}

	public KFunc Raw { get; }

	public KValue Value => Raw.Value;

	public TResult Invoke(Interpreter interpreter, T argument) =>
		Marshalling.KMarshal.To<TResult>(Raw.Invoke(interpreter, Marshalling.KMarshal.From(argument)));

	public static implicit operator KValue(KFunc<T, TResult> function) => function.Value;
}

/// <summary>Действие `fun() -&gt; void`.</summary>
public readonly struct KAction
{
	public KAction(KValue value)
	{
		Raw = new KFunc(value);
	}

	public KFunc Raw { get; }

	public KValue Value => Raw.Value;

	public void Invoke(Interpreter interpreter) => Raw.Invoke(interpreter);

	public static implicit operator KValue(KAction action) => action.Value;
}

/// <summary>Действие `fun(A) -&gt; void`.</summary>
public readonly struct KAction<T>
{
	public KAction(KValue value)
	{
		Raw = new KFunc(value);
	}

	public KFunc Raw { get; }

	public KValue Value => Raw.Value;

	public void Invoke(Interpreter interpreter, T argument) =>
		Raw.Invoke(interpreter, Marshalling.KMarshal.From(argument));

	public static implicit operator KValue(KAction<T> action) => action.Value;
}
