using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Общий протокол контейнеров <c>collection&lt;T&gt;</c> (spec §16): его реализуют
/// <c>set</c>/<c>queue</c>/<c>stack</c>/<c>observable</c>, а <c>list</c>/<c>array</c>/<c>dict</c>/<c>str</c>
/// обслуживаются напрямую по VM-значению. Позволяет писать функции над любым контейнером.
/// </summary>
public interface IKernCollection
{
	int Count { get; }

	IEnumerable<KValue> Items { get; }

	bool Contains(KValue item);
}

/// <summary>
/// Члены встроенного <c>collection&lt;T&gt;</c> (spec §16): <c>len</c>, <c>is_empty</c>,
/// <c>contains</c>, <c>to_list</c>. Приёмник — <see cref="KValue"/>: конкретный вид
/// (list/array/dict/str или нативный контейнер) определяется во время выполнения.
/// </summary>
[KernType("collection", IsBuiltin = true, GenericArity = 1)]
public static class CollectionApi
{
	/// <summary><c>len</c>: число элементов (для <c>str</c> — длина строки).</summary>
	[KernProperty]
	public static int Len(KValue self) => CollectionOps.Count(self);

	/// <summary><c>is_empty</c>: контейнер пуст.</summary>
	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KValue self) => CollectionOps.Count(self) == 0;

	/// <summary><c>contains(item)</c>: есть ли элемент (его же использует <c>in</c>).</summary>
	[KernMember]
	public static bool Contains(KValue self, KValue item) => CollectionOps.Contains(self, item);

	/// <summary><c>to_list()</c>: снимок элементов как <c>list&lt;T&gt;</c>.</summary>
	[KernName("to_list")]
	[KernMember]
	public static KValue ToList(KValue self) => CollectionOps.ListOf(self);
}
