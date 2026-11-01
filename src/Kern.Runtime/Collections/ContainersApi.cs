using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Множество Kern-типа <c>set&lt;T&gt;</c> (spec §16): уникальные значения в порядке
/// добавления, равенство — по <see cref="KValue"/> (как у <c>list</c>). Значение живёт
/// в VM внутри <see cref="KNativeBox"/>; члены — в <see cref="SetApi"/>.
/// </summary>
[KernType("set", GenericArity = 1)]
public sealed class KernSet
{
	private readonly List<KValue> _items = [];

	/// <summary>Число элементов (без повторов).</summary>
	public int Count => _items.Count;

	/// <summary>Элементы в порядке добавления; менять напрямую нельзя.</summary>
	public IReadOnlyList<KValue> Items => _items;

	/// <summary>Добавить значение; повтор (по равенству <see cref="KValue"/>) игнорируется.</summary>
	public void Add(KValue item)
	{
		if (!_items.Contains(item))
			_items.Add(item);
	}

	/// <summary>Удалить значение; <c>true</c>, если оно было.</summary>
	public bool Remove(KValue item) => _items.Remove(item);

	/// <summary>Есть ли значение.</summary>
	public bool Contains(KValue item) => _items.Contains(item);

	/// <summary>Очистить множество.</summary>
	public void Clear() => _items.Clear();

	/// <summary>Копия элементов как <c>list&lt;T&gt;</c>.</summary>
	public KList ToList() => new(null, _items);

	public override string ToString() => $"set({_items.Count})";
}

/// <summary>
/// Члены и фабрика Kern-типа <c>set&lt;T&gt;</c> (spec §16): <c>add</c>, <c>remove</c>,
/// <c>contains</c>, <c>clear</c>, свойства <c>len</c>/<c>is_empty</c>, <c>to_list</c>.
/// Первый параметр членов — <c>self</c> из <see cref="KNativeBox"/>; элемент приходит как
/// <see cref="KValue"/>, а типом <c>T</c> его делает binder (generic-аргумент получателя).
/// </summary>
[KernType("set", GenericArity = 1)]
public static class SetApi
{
	/// <summary><c>set&lt;T&gt;()</c>: пустое множество.</summary>
	[KernCtor]
	[KernName("set")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernSet()));

	/// <summary><c>len</c>: число элементов.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>is_empty</c>: множество пусто.</summary>
	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KNativeBox self) => Require(self).Count == 0;

	/// <summary><c>add(item)</c>: добавить, повтор игнорируется.</summary>
	[KernMember]
	public static void Add(KNativeBox self, KValue item) => Require(self).Add(item);

	/// <summary><c>remove(item)</c>: удалить; <c>true</c>, если элемент был.</summary>
	[KernMember]
	public static bool Remove(KNativeBox self, KValue item) => Require(self).Remove(item);

	/// <summary><c>contains(item)</c>: есть ли элемент (его же использует <c>in</c>).</summary>
	[KernMember]
	public static bool Contains(KNativeBox self, KValue item) => Require(self).Contains(item);

	/// <summary><c>clear()</c>: очистить множество.</summary>
	[KernMember]
	public static void Clear(KNativeBox self) => Require(self).Clear();

	/// <summary><c>to_list()</c>: элементы в порядке добавления как <c>list&lt;T&gt;</c>.</summary>
	[KernName("to_list")]
	[KernMember]
	public static KValue ToList(KNativeBox self) => KValue.Obj(Require(self).ToList());

	private static KernSet Require(KNativeBox self) => self.Value switch
	{
		KernSet set => set,
		_ => throw new KernPanic($"ожидался set, получено {self.Value.GetType().Name}"),
	};
}
