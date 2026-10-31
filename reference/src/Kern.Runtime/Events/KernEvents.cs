using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Values;

namespace Kern.Runtime.Events;

/// <summary>
/// Событие с одним аргументом (делегат + подписки в одном типе, без новых ключевых слов):
/// `event&lt;str&gt; changed = event&lt;str&gt;()`; `changed += fun(str s) { … }`; `changed -= h`; `changed.emit("x")`.
/// Операторы `+`/`-` изменяют само событие и возвращают его же, поэтому `obj.changed += h` (присваивание обратно) безвредно.
/// </summary>
[KernType("event")]
public sealed class KernEvent<T>
{
	private readonly List<KAction<T>> _handlers = [];

	[KernCtor] public KernEvent() { }

	[KernProperty] public int Len => _handlers.Count;

	[KernOperator("+")] public KernEvent<T> Subscribe(KAction<T> handler)
	{
		_handlers.Add(handler);
		return this;
	}

	[KernOperator("-")] public KernEvent<T> Unsubscribe(KAction<T> handler)
	{
		int i = _handlers.FindIndex(h => KValueComparer.Instance.Equals(h.Inner.Function, handler.Inner.Function));
		if (i >= 0) _handlers.RemoveAt(i);
		return this;
	}

	/// <summary>Вызывает все обработчики в порядке подписки (по снимку списка).</summary>
	[KernMember] public void Emit(T arg)
	{
		var v = Kv.Of(arg);
		foreach (var h in _handlers.ToArray()) h.Invoke(v);
	}

	[KernMember] public void Clear() => _handlers.Clear();
}

/// <summary>Событие без аргументов: `signal done = signal()`; `done += fun { … }`; `done.emit()`.</summary>
[KernType("signal")]
public sealed class KernSignal
{
	private readonly List<KAction> _handlers = [];

	[KernCtor] public KernSignal() { }

	[KernProperty] public int Len => _handlers.Count;

	[KernOperator("+")] public KernSignal Subscribe(KAction handler)
	{
		_handlers.Add(handler);
		return this;
	}

	[KernOperator("-")] public KernSignal Unsubscribe(KAction handler)
	{
		int i = _handlers.FindIndex(h => KValueComparer.Instance.Equals(h.Inner.Function, handler.Inner.Function));
		if (i >= 0) _handlers.RemoveAt(i);
		return this;
	}

	[KernMember] public void Emit()
	{
		foreach (var h in _handlers.ToArray()) h.Invoke();
	}

	[KernMember] public void Clear() => _handlers.Clear();
}
