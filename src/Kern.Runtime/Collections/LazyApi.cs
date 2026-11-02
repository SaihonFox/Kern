using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Лениво вычисляемое значение <c>lazy&lt;T&gt;</c> (spec §16): фабричная функция
/// вызывается при первом обращении к <c>value</c> ровно один раз. Вычисление идёт под
/// замком, поэтому повторные обращения из разных потоков видят одно и то же значение.
/// </summary>
[KernType("lazy", GenericArity = 1)]
public sealed class KernLazy
{
	private readonly Lock _gate = new();
	private readonly Interpreter _interpreter;
	private readonly KValue _factory;
	private KValue _value = KValue.None;
	private bool _ready;

	/// <summary>Создать ленивое значение из функции <c>fun() -&gt; T</c>.</summary>
	public KernLazy(Interpreter interpreter, KValue factory)
	{
		_interpreter = interpreter;
		_factory = factory;
	}

	/// <summary>Значение; при первом обращении вычисляет фабрику.</summary>
	public KValue Value
	{
		get
		{
			lock (_gate)
			{
				if (!_ready)
				{
					_value = _interpreter.RunValue(_factory);
					_ready = true;
				}

				return _value;
			}
		}
	}

	/// <summary>Вычислено ли значение.</summary>
	public bool IsReady
	{
		get
		{
			lock (_gate) return _ready;
		}
	}
}

/// <summary>
/// Фабрика и члены <c>lazy&lt;T&gt;</c> (spec §16): <c>lazy(fun() -&gt; T)</c>,
/// свойство <c>value -&gt; T</c> и <c>is_ready</c>.
/// </summary>
[KernType("lazy", GenericArity = 1)]
public static class LazyApi
{
	/// <summary><c>lazy(f)</c>: значение, вычисляемое при первом обращении.</summary>
	[KernCtor]
	[KernName("lazy")]
	[KernReturn("lazy<T>")]
	public static KValue Create<T>(
		Interpreter interpreter,
		[KernParameterType("fun()->T")] KValue function) =>
		KValue.Obj(new KNativeBox(new KernLazy(interpreter, function)));

	/// <summary><c>value</c>: вычисленное (и закэшированное) значение.</summary>
	[KernProperty]
	[KernReturn("T")]
	public static KValue Value(KNativeBox self) => Require(self).Value;

	/// <summary><c>is_ready</c>: значение уже вычислено.</summary>
	[KernName("is_ready")]
	[KernProperty]
	public static bool IsReady(KNativeBox self) => Require(self).IsReady;

	private static KernLazy Require(KNativeBox self) => self.Value switch
	{
		KernLazy lazy => lazy,
		_ => throw new KernPanic($"ожидался lazy, получено {self.Value.GetType().Name}"),
	};
}
