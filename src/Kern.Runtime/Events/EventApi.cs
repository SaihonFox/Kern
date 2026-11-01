using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Events;

/// <summary>
/// Событие <c>event&lt;T&gt;</c> (spec round 16): подписки хранятся как функции-значения
/// VM, вызов идёт в интерпретаторе подписчика. Операторы <c>+</c>/<c>-</c> мутируют
/// событие и возвращают его же, поэтому <c>obj.changed += handler</c> работает без
/// новых ключевых слов.
/// </summary>
[KernType("event", GenericArity = 1)]
public sealed class KernEvent
{
	private readonly List<KValue> _handlers = [];

	/// <summary>Число подписчиков.</summary>
	public int Count => _handlers.Count;

	/// <summary>Подписать обработчик (порядок вызова — порядок подписки).</summary>
	public void Add(KValue handler) => _handlers.Add(handler);

	/// <summary>Отписать первый равный обработчик; <c>true</c>, если он был.</summary>
	public bool Remove(KValue handler) => _handlers.Remove(handler);

	/// <summary>Вызвать всех подписчиков по снимку списка.</summary>
	public void Emit(Interpreter interpreter, KValue argument)
	{
		foreach (KValue handler in _handlers.ToArray())
			interpreter.RunValue(handler, argument);
	}

	/// <summary>Убрать все подписки.</summary>
	public void Clear() => _handlers.Clear();

	public override string ToString() => $"event({_handlers.Count})";
}

/// <summary>Событие без аргументов: <c>signal</c> (spec round 16).</summary>
[KernType("signal")]
public sealed class KernSignal
{
	private readonly List<KValue> _handlers = [];

	/// <summary>Число подписчиков.</summary>
	public int Count => _handlers.Count;

	/// <summary>Подписать обработчик <c>fun()</c>.</summary>
	public void Add(KValue handler) => _handlers.Add(handler);

	/// <summary>Отписать первый равный обработчик.</summary>
	public bool Remove(KValue handler) => _handlers.Remove(handler);

	/// <summary>Вызвать всех подписчиков.</summary>
	public void Emit(Interpreter interpreter)
	{
		foreach (KValue handler in _handlers.ToArray())
			interpreter.RunValue(handler);
	}

	/// <summary>Убрать все подписки.</summary>
	public void Clear() => _handlers.Clear();

	public override string ToString() => $"signal({_handlers.Count})";
}

/// <summary>Фабрика и члены <c>event&lt;T&gt;</c> (spec round 16).</summary>
[KernType("event", GenericArity = 1)]
public static class EventApi
{
	/// <summary><c>event&lt;T&gt;()</c>: пустое событие.</summary>
	[KernCtor]
	[KernName("event")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernEvent()));

	/// <summary><c>len</c>: число подписчиков.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>ev += handler</c>: подписать; возвращает само событие.</summary>
	[KernMember(Operator = "+")]
	[KernReturn("event<T>")]
	public static KValue Subscribe(KNativeBox self, KValue handler)
	{
		Require(self).Add(handler);
		return KValue.Obj(self);
	}

	/// <summary><c>ev -= handler</c>: отписать; возвращает само событие.</summary>
	[KernMember(Operator = "-")]
	[KernReturn("event<T>")]
	public static KValue Unsubscribe(KNativeBox self, KValue handler)
	{
		Require(self).Remove(handler);
		return KValue.Obj(self);
	}

	/// <summary><c>emit(arg)</c>: вызвать подписчиков в порядке подписки.</summary>
	[KernMember]
	public static void Emit(Interpreter interpreter, KNativeBox self, KValue argument) =>
		Require(self).Emit(interpreter, argument);

	/// <summary><c>clear()</c>: убрать все подписки.</summary>
	[KernMember]
	public static void Clear(KNativeBox self) => Require(self).Clear();

	private static KernEvent Require(KNativeBox self) => self.Value switch
	{
		KernEvent value => value,
		_ => throw new KernPanic($"ожидался event, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Фабрика и члены <c>signal</c> (spec round 16).</summary>
[KernType("signal")]
public static class SignalApi
{
	/// <summary><c>signal()</c>: пустой сигнал.</summary>
	[KernCtor]
	[KernName("signal")]
	[KernReturn("signal")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernSignal()));

	/// <summary><c>len</c>: число подписчиков.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>sig += handler</c>: подписать <c>fun()</c>.</summary>
	[KernMember(Operator = "+")]
	[KernReturn("signal")]
	public static KValue Subscribe(KNativeBox self, KValue handler)
	{
		Require(self).Add(handler);
		return KValue.Obj(self);
	}

	/// <summary><c>sig -= handler</c>: отписать.</summary>
	[KernMember(Operator = "-")]
	[KernReturn("signal")]
	public static KValue Unsubscribe(KNativeBox self, KValue handler)
	{
		Require(self).Remove(handler);
		return KValue.Obj(self);
	}

	/// <summary><c>emit()</c>: вызвать всех подписчиков.</summary>
	[KernMember]
	public static void Emit(Interpreter interpreter, KNativeBox self) => Require(self).Emit(interpreter);

	/// <summary><c>clear()</c>: убрать все подписки.</summary>
	[KernMember]
	public static void Clear(KNativeBox self) => Require(self).Clear();

	private static KernSignal Require(KNativeBox self) => self.Value switch
	{
		KernSignal value => value,
		_ => throw new KernPanic($"ожидался signal, получено {self.Value.GetType().Name}"),
	};
}
