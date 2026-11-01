using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Наблюдаемый список <c>observable&lt;T&gt;</c> (spec §16, пример 22): хранит элементы и
/// подписчиков <c>on_change(kind, item)</c>. Каждое изменение (<c>add</c>, <c>remove</c>,
/// <c>clear</c>) вызывает подписчиков синхронно в порядке регистрации.
/// </summary>
[KernType("observable", GenericArity = 1)]
public sealed class KernObservable : IKernCollection
{
	private readonly List<KValue> _items = [];
	private readonly List<KValue> _listeners = [];

	/// <summary>Число элементов.</summary>
	public int Count => _items.Count;

	/// <summary>Снимок элементов.</summary>
	public IEnumerable<KValue> Items => _items;

	/// <summary>Есть ли элемент.</summary>
	public bool Contains(KValue item) => _items.Contains(item);

	/// <summary>Подписать функцию <c>fun(str, T)</c> на изменения.</summary>
	public void Listen(KValue callback) => _listeners.Add(callback);

	/// <summary>Добавить элемент и уведомить подписчиков.</summary>
	public void Add(Interpreter interpreter, KValue item)
	{
		_items.Add(item);
		Notify(interpreter, "add", item);
	}

	/// <summary>Удалить элемент (первое вхождение); уведомить, если он был.</summary>
	public bool Remove(Interpreter interpreter, KValue item)
	{
		if (!_items.Remove(item))
			return false;
		Notify(interpreter, "remove", item);
		return true;
	}

	/// <summary>Очистить список; подписчики получают <c>clear</c> и <c>none</c> вместо элемента.</summary>
	public void Clear(Interpreter interpreter)
	{
		_items.Clear();
		Notify(interpreter, "clear", KValue.None);
	}

	private void Notify(Interpreter interpreter, string kind, KValue item)
	{
		foreach (KValue listener in _listeners.ToArray())
			interpreter.RunValue(listener, KValue.Str(kind), item);
	}
}

/// <summary>Члены и фабрика <c>observable&lt;T&gt;</c>.</summary>
[KernType("observable", GenericArity = 1)]
public static class ObservableApi
{
	/// <summary><c>observable&lt;T&gt;()</c>: пустой наблюдаемый список.</summary>
	[KernCtor]
	[KernName("observable")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernObservable()));

	/// <summary><c>len</c>: число элементов.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>is_empty</c>: список пуст.</summary>
	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KNativeBox self) => Require(self).Count == 0;

	/// <summary><c>add(item)</c>: добавить и уведомить подписчиков.</summary>
	[KernMember]
	public static void Add(Interpreter interpreter, KNativeBox self, KValue item) =>
		Require(self).Add(interpreter, item);

	/// <summary><c>remove(item)</c>: удалить; <c>true</c>, если элемент был.</summary>
	[KernMember]
	public static bool Remove(Interpreter interpreter, KNativeBox self, KValue item) =>
		Require(self).Remove(interpreter, item);

	/// <summary><c>clear()</c>: очистить и уведомить подписчиков.</summary>
	[KernMember]
	public static void Clear(Interpreter interpreter, KNativeBox self) => Require(self).Clear(interpreter);

	/// <summary><c>contains(item)</c>: есть ли элемент.</summary>
	[KernMember]
	public static bool Contains(KNativeBox self, KValue item) => Require(self).Contains(item);

	/// <summary><c>on_change(fun(str kind, T item))</c>: подписаться на изменения.</summary>
	[KernName("on_change")]
	[KernMember]
	public static void OnChange(KNativeBox self, KValue callback) => Require(self).Listen(callback);

	/// <summary><c>to_list()</c>: снимок элементов.</summary>
	[KernName("to_list")]
	[KernMember]
	public static KValue ToList(KNativeBox self) =>
		KValue.Obj(new Kern.Vm.Values.KList(null, Require(self).Items));

	private static KernObservable Require(KNativeBox self) => self.Value switch
	{
		KernObservable observable => observable,
		_ => throw new KernPanic($"ожидался observable, получено {self.Value.GetType().Name}"),
	};
}
