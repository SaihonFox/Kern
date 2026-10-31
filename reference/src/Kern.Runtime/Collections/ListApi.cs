using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>Члены list&lt;T&gt;: доступ, изменение, сортировка. Запросы (map/filter/fold…) — в ListApi.Query.</summary>
[KernType("list")]
public static partial class ListApi
{
	private static KernPanic Empty(string op) => new($"{op}: список пуст");
	private static KernPanic OutOfRange(int i, int n) => new($"индекс {i} вне диапазона списка длины {n}");

	// ---------- свойства ----------
	[KernProperty] public static int Len<T>(KList<T> self) => self.Count;
	[KernProperty] public static bool IsEmpty<T>(KList<T> self) => self.Count == 0;
	[KernProperty] public static T First<T>(KList<T> self) => self.Count > 0 ? Kv.As<T>(self[0]) : throw Empty("first");
	[KernProperty] public static T Last<T>(KList<T> self) => self.Count > 0 ? Kv.As<T>(self[^1]) : throw Empty("last");
	[KernProperty] public static KList<T> Reversed<T>(KList<T> self) => Same(self, Enumerable.Reverse(self.Items));
	[KernProperty] public static KList<T> Sorted<T>(KList<T> self) => Same(self, self.Items.OrderBy(x => x, Comparer<KValue>.Create(Kv.Cmp)));
	[KernProperty] public static KList<T> Distinct<T>(KList<T> self) => Same(self, self.Items.Distinct(KValueComparer.Instance));

	// ---------- доступ ----------
	[KernMember] public static T Get<T>(KList<T> self, int index) =>
		(uint)index < (uint)self.Count ? Kv.As<T>(self[index]) : throw OutOfRange(index, self.Count);
	[KernMember] public static void Set<T>(KList<T> self, int index, T value)
	{
		if ((uint)index >= (uint)self.Count) throw OutOfRange(index, self.Count);
		self[index] = Kv.Of(value);
	}
	[KernMember] public static T? GetOrNil<T>(KList<T> self, int index) => (uint)index < (uint)self.Count ? Kv.As<T>(self[index]) : Kv.As<T>(KValue.Nil);

	// ---------- изменение ----------
	[KernMember] public static KList<T> Add<T>(KList<T> self, T item) { self.Items.Add(Kv.Of(item)); return self; }
	[KernMember] public static KList<T> AddRange<T>(KList<T> self, KList<T> items) { self.Items.AddRange(items.Items.ToArray()); return self; }
	[KernMember] public static void Insert<T>(KList<T> self, int index, T item)
	{
		if ((uint)index > (uint)self.Count) throw OutOfRange(index, self.Count);
		self.Items.Insert(index, Kv.Of(item));
	}
	[KernMember] public static bool Remove<T>(KList<T> self, T item)
	{
		int i = IndexOfCore(self, Kv.Of(item));
		if (i < 0) return false;
		self.Items.RemoveAt(i);
		return true;
	}
	[KernMember] public static T RemoveAt<T>(KList<T> self, int index)
	{
		if ((uint)index >= (uint)self.Count) throw OutOfRange(index, self.Count);
		var v = self[index];
		self.Items.RemoveAt(index);
		return Kv.As<T>(v);
	}
	[KernMember] public static T Pop<T>(KList<T> self) => self.Count == 0 ? throw Empty("pop") : RemoveAt(self, self.Count - 1);
	[KernMember] public static void Clear<T>(KList<T> self) => self.Items.Clear();
	[KernMember] public static void Reverse<T>(KList<T> self) => self.Items.Reverse();
	[KernMember] public static void Sort<T>(KList<T> self)
	{
		var sorted = self.Items.OrderBy(x => x, Comparer<KValue>.Create(Kv.Cmp)).ToList();   // стабильная сортировка
		self.Items.Clear();
		self.Items.AddRange(sorted);
	}
	[KernMember] public static void Shuffle<T>(KList<T> self)
	{
		var l = self.Items;
		for (int i = l.Count - 1; i > 0; i--) { int j = System.Random.Shared.Next(i + 1); (l[i], l[j]) = (l[j], l[i]); }
	}

	// ---------- поиск ----------
	[KernMember] public static bool Contains<T>(KList<T> self, T item) => IndexOfCore(self, Kv.Of(item)) >= 0;
	[KernMember] public static int IndexOf<T>(KList<T> self, T item) => IndexOfCore(self, Kv.Of(item));
	[KernMember] public static int LastIndexOf<T>(KList<T> self, T item)
	{
		var v = Kv.Of(item);
		for (int i = self.Count - 1; i >= 0; i--) if (Kv.Eq(self[i], v)) return i;
		return -1;
	}

	private static int IndexOfCore<T>(KList<T> self, KValue v)
	{
		var l = self.Items;
		for (int i = 0; i < l.Count; i++) if (Kv.Eq(l[i], v)) return i;
		return -1;
	}

	// ---------- срезы и копии ----------
	/// <summary>Подсписок [start, start+count); count &lt; 0 — до конца. Отрицательный start — с конца.</summary>
	[KernMember] public static KList<T> Slice<T>(KList<T> self, int start, int count = -1)
	{
		int n = self.Count;
		int s = System.Math.Clamp(start < 0 ? n + start : start, 0, n);
		int c = count < 0 ? n - s : System.Math.Min(count, n - s);
		return Same(self, self.Items.GetRange(s, c));
	}
	[KernMember] public static KList<T> Copy<T>(KList<T> self) => Same(self, self.Items);
	[KernMember] public static string Join<T>(KList<T> self, string separator = "") => string.Join(separator, self.Items.Select(ValueOps.ToStr));
	[KernMember] public static string ToStr<T>(KList<T> self) => ValueOps.ToStr(KValue.Obj(self.Inner));

	/// <summary>Новый список с тем же типом элементов.</summary>
	internal static KList<T> Same<T>(KList<T> like, IEnumerable<KValue> items) => new(new KListObject(like.Inner.ElementType, [.. items]));

	// ---------- создание ----------
	/// <summary>list.repeat(value, n) — список из n копий значения.</summary>
	[KernStatic] public static KList<T> Repeat<T>(T value, int count, [KernTypeArgs] KType[] typeArgs) =>
		Kv.List<T>(Kv.Last(typeArgs), Enumerable.Repeat(Kv.Of(value), System.Math.Max(0, count)));
}
