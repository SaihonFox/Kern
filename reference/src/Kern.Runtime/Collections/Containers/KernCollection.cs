using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections.Containers;

/// <summary>
/// Общий базовый тип `collection&lt;T&gt;` для set / queue / stack / observable: `fun total(collection&lt;i32&gt; c)` принимает любую из них.
/// Наследование нативных типов берётся из C# (`: KernCollection&lt;T&gt;`). Встроенные list/array/str/dict/range тоже
/// являются потомками: на границе с нативным кодом они оборачиваются в <see cref="KernCollectionAdapter"/>.
/// </summary>
[KernType("collection")]
[KernAdapter(typeof(KernCollectionAdapterFactory))]
public abstract class KernCollection<T>
{
	[KernProperty] public abstract int Len { get; }
	[KernProperty] public virtual bool IsEmpty => Len == 0;

	[KernMember] public abstract void Clear();

	/// <summary>Для `loop var x in c`.</summary>
	[KernMember] public abstract KList<T> Iter();

	[KernMember] public virtual KList<T> ToList() => Iter();

	[KernMember] public virtual bool Contains(T item)
	{
		var v = Kv.Of(item);
		return Iter().Items.Contains(v, KValueComparer.Instance);
	}

	[KernOperator("in")] public virtual bool In(T item) => Contains(item);
}
