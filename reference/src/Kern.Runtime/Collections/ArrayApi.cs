using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>Члены array&lt;T&gt; (фиксированная длина).</summary>
[KernType("array")]
public static class ArrayApi
{
	private static KernPanic OutOfRange(int i, int n) => new($"индекс {i} вне диапазона массива длины {n}");

	[KernStatic] public static KArray<T> Create<T>(int length, T fill, [KernTypeArgs] KType[] typeArgs)
	{
		if (length < 0) throw new KernPanic("длина массива не может быть отрицательной");
		var items = new KValue[length];
		Array.Fill(items, Kv.Of(fill));
		return new KArray<T>(new KArrayObject(Kv.Last(typeArgs), items));
	}

	[KernProperty] public static int Len<T>(KArray<T> self) => self.Items.Length;
	[KernProperty] public static bool IsEmpty<T>(KArray<T> self) => self.Items.Length == 0;

	[KernMember] public static T Get<T>(KArray<T> self, int index) =>
		(uint)index < (uint)self.Items.Length ? Kv.As<T>(self.Items[index]) : throw OutOfRange(index, self.Items.Length);

	[KernMember] public static void Set<T>(KArray<T> self, int index, T value)
	{
		if ((uint)index >= (uint)self.Items.Length) throw OutOfRange(index, self.Items.Length);
		self.Items[index] = Kv.Of(value);
	}

	[KernMember] public static bool Contains<T>(KArray<T> self, T item)
	{
		var v = Kv.Of(item);
		return self.Items.Any(x => Kv.Eq(x, v));
	}

	[KernMember] public static void Fill<T>(KArray<T> self, T value) => Array.Fill(self.Items, Kv.Of(value));

	[KernMember] public static void Reverse<T>(KArray<T> self) => Array.Reverse(self.Items);

	[KernMember] public static void Sort<T>(KArray<T> self) => Array.Sort(self.Items, Kv.Cmp);

	[KernMember] public static KArray<T> Copy<T>(KArray<T> self) => new(new KArrayObject(self.Inner.ElementType, (KValue[])self.Items.Clone()));

	[KernMember] public static KList<T> ToList<T>(KArray<T> self) => Kv.List<T>(self.Inner.ElementType, self.Items);
}
