using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

// LINQ-подобные запросы. Все возвращают новые списки; исходный не меняется. Лямбды вызываются синхронно через VM.
public static partial class ListApi
{
	private static readonly Comparer<KValue> ValueComparer = Comparer<KValue>.Create(Kv.Cmp);

	// ---------- преобразование ----------
	[KernMember] public static KList<R> Map<T, R>(KList<T> self, KFunc<T, R> f, [KernTypeArgs] KType[] typeArgs)
	{
		var res = new List<KValue>(self.Count);
		foreach (var x in self.Items.ToArray()) res.Add(f.Invoke(x));
		return Kv.List<R>(Kv.Last(typeArgs), res);
	}

	[KernMember] public static KList<KTuple<int, T>> Enumerate<T>(KList<T> self) =>
		Kv.List<KTuple<int, T>>(self.Items.Select((x, i) => KValue.Obj(Kv.Tuple(KValue.I32(i), x))));

	[KernMember] public static KList<T> Filter<T>(KList<T> self, KFunc<T, bool> predicate) =>
		Same(self, self.Items.ToArray().Where(x => Kv.Truthy(predicate.Invoke(x))));

	[KernMember] public static void Each<T>(KList<T> self, KAction<T> action)
	{
		foreach (var x in self.Items.ToArray()) action.Invoke(x);
	}

	[KernMember] public static void EachIndexed<T>(KList<T> self, KAction<int, T> action)
	{
		var snapshot = self.Items.ToArray();
		for (int i = 0; i < snapshot.Length; i++) action.Invoke(KValue.I32(i), snapshot[i]);
	}

	[KernMember] public static KList<R> FlatMap<T, R>(KList<T> self, KFunc<T, KList<R>> f, [KernTypeArgs] KType[] typeArgs)
	{
		var res = new List<KValue>();
		foreach (var x in self.Items.ToArray())
			if (f.Invoke(x).Ref is KListObject inner) res.AddRange(inner.Items);
		return Kv.List<R>(Kv.Last(typeArgs), res);
	}

	[KernMember] public static KList<T> Flatten<T>(KList<KList<T>> self) =>
		Kv.List<T>(self.Items.SelectMany(x => x.Ref is KListObject l ? l.Items : []));

	// ---------- свёртки ----------
	[KernMember] public static U Fold<T, U>(KList<T> self, U initial, KFunc<U, T, U> combine)
	{
		var acc = Kv.Of(initial);
		foreach (var x in self.Items.ToArray()) acc = combine.Invoke(acc, x);
		return Kv.As<U>(acc);
	}

	[KernMember] public static T Reduce<T>(KList<T> self, KFunc<T, T, T> combine)
	{
		if (self.Count == 0) throw Empty("reduce");
		var acc = self[0];
		foreach (var x in self.Items.Skip(1).ToArray()) acc = combine.Invoke(acc, x);
		return Kv.As<T>(acc);
	}

	/// <summary>Сумма числового списка; тип результата = тип первого элемента (float, если встретился float). Пустой → 0.</summary>
	[KernProperty] public static T Sum<T>(KList<T> self)
	{
		if (self.Count == 0) return Kv.As<T>(KValue.I32(0));
		var kind = self[0].Kind;
		if (self.Items.Any(x => x.IsFloat)) return Kv.As<T>(Num.FromDouble(Num.IsFloat(kind) ? kind : ValueKind.F64, self.Items.Sum(x => x.AsF64)));
		long acc = 0;
		foreach (var x in self.Items) acc = unchecked(acc + x.Bits);
		return Kv.As<T>(Num.Wrap(kind, acc));
	}

	[KernProperty] public static double Average<T>(KList<T> self) => self.Count == 0 ? 0 : self.Items.Average(x => x.AsF64);

	[KernProperty] public static T Min<T>(KList<T> self) => self.Count == 0 ? throw Empty("min") : Kv.As<T>(self.Items.Min(ValueComparer));
	[KernProperty] public static T Max<T>(KList<T> self) => self.Count == 0 ? throw Empty("max") : Kv.As<T>(self.Items.Max(ValueComparer));

	[KernMember] public static T MinBy<T, K>(KList<T> self, KFunc<T, K> key)
	{
		if (self.Count == 0) throw Empty("min_by");
		var best = self[0]; var bk = key.Invoke(best);
		foreach (var x in self.Items.Skip(1).ToArray()) { var k = key.Invoke(x); if (Kv.Compare(k, bk) < 0) (best, bk) = (x, k); }
		return Kv.As<T>(best);
	}

	[KernMember] public static T MaxBy<T, K>(KList<T> self, KFunc<T, K> key)
	{
		if (self.Count == 0) throw Empty("max_by");
		var best = self[0]; var bk = key.Invoke(best);
		foreach (var x in self.Items.Skip(1).ToArray()) { var k = key.Invoke(x); if (Kv.Compare(k, bk) > 0) (best, bk) = (x, k); }
		return Kv.As<T>(best);
	}

	// ---------- проверки и поиск ----------
	[KernMember] public static bool Any<T>(KList<T> self) => self.Count > 0;
	[KernMember] public static bool Any<T>(KList<T> self, KFunc<T, bool> predicate) => self.Items.ToArray().Any(x => Kv.Truthy(predicate.Invoke(x)));
	[KernMember] public static bool All<T>(KList<T> self, KFunc<T, bool> predicate) => self.Items.ToArray().All(x => Kv.Truthy(predicate.Invoke(x)));
	[KernMember] public static bool None<T>(KList<T> self, KFunc<T, bool> predicate) => !Any(self, predicate);
	[KernMember] public static int CountIf<T>(KList<T> self, KFunc<T, bool> predicate) => self.Items.ToArray().Count(x => Kv.Truthy(predicate.Invoke(x)));

	[KernProperty] public static T? FirstOrNil<T>(KList<T> self) => self.Count > 0 ? Kv.As<T>(self[0]) : Kv.As<T>(KValue.Nil);
	[KernProperty] public static T? LastOrNil<T>(KList<T> self) => self.Count > 0 ? Kv.As<T>(self[^1]) : Kv.As<T>(KValue.Nil);

	[KernMember] public static T? Find<T>(KList<T> self, KFunc<T, bool> predicate)
	{
		foreach (var x in self.Items.ToArray()) if (Kv.Truthy(predicate.Invoke(x))) return Kv.As<T>(x);
		return Kv.As<T>(KValue.Nil);
	}

	[KernMember] public static int FindIndex<T>(KList<T> self, KFunc<T, bool> predicate)
	{
		var a = self.Items.ToArray();
		for (int i = 0; i < a.Length; i++) if (Kv.Truthy(predicate.Invoke(a[i]))) return i;
		return -1;
	}

	// ---------- упорядочивание и группировка ----------
	[KernMember] public static KList<T> OrderBy<T, K>(KList<T> self, KFunc<T, K> key) => Same(self, SortedByKey(self, key, false));
	[KernMember] public static KList<T> OrderByDesc<T, K>(KList<T> self, KFunc<T, K> key) => Same(self, SortedByKey(self, key, true));

	private static IEnumerable<KValue> SortedByKey<T, K>(KList<T> self, KFunc<T, K> key, bool desc)
	{
		var keyed = self.Items.ToArray().Select(x => (Item: x, Key: key.Invoke(x))).ToArray();   // ключ считается один раз
		return desc ? keyed.OrderByDescending(p => p.Key, ValueComparer).Select(p => p.Item)
					: keyed.OrderBy(p => p.Key, ValueComparer).Select(p => p.Item);
	}

	[KernMember] public static KList<T> DistinctBy<T, K>(KList<T> self, KFunc<T, K> key)
	{
		var seen = new HashSet<KValue>(KValueComparer.Instance);
		return Same(self, self.Items.ToArray().Where(x => seen.Add(key.Invoke(x))));
	}

	/// <summary>Группы в порядке первого появления ключа: dict&lt;K, list&lt;T&gt;&gt;.</summary>
	[KernMember] public static KDict<K, KList<T>> GroupBy<T, K>(KList<T> self, KFunc<T, K> key, [KernTypeArgs] KType[] typeArgs)
	{
		var dict = new KDictObject(Kv.Obj, Kv.Obj);
		foreach (var x in self.Items.ToArray())
		{
			var k = key.Invoke(x);
			if (!dict.Items.TryGetValue(k, out var g)) dict.Items[k] = g = KValue.Obj(new KListObject(self.Inner.ElementType));
			((KListObject)g.Ref!).Items.Add(x);
		}
		return new(dict);
	}

	// ---------- части списка ----------
	[KernMember] public static KList<T> Take<T>(KList<T> self, int count) => Same(self, self.Items.Take(System.Math.Max(0, count)));
	[KernMember] public static KList<T> Skip<T>(KList<T> self, int count) => Same(self, self.Items.Skip(System.Math.Max(0, count)));
	[KernMember] public static KList<T> TakeWhile<T>(KList<T> self, KFunc<T, bool> predicate) => Same(self, self.Items.ToArray().TakeWhile(x => Kv.Truthy(predicate.Invoke(x))));
	[KernMember] public static KList<T> SkipWhile<T>(KList<T> self, KFunc<T, bool> predicate) => Same(self, self.Items.ToArray().SkipWhile(x => Kv.Truthy(predicate.Invoke(x))));

	[KernMember] public static KList<KList<T>> Chunk<T>(KList<T> self, int size)
	{
		if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size), "размер куска должен быть > 0");
		return Kv.List<KList<T>>(self.Items.Chunk(size).Select(c => KValue.Obj(Same(self, c).Inner)));
	}

	/// <summary>Скользящие окна длины size (пусто, если список короче).</summary>
	[KernMember] public static KList<KList<T>> Window<T>(KList<T> self, int size)
	{
		if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size), "размер окна должен быть > 0");
		var res = new List<KValue>();
		for (int i = 0; i + size <= self.Count; i++) res.Add(KValue.Obj(Same(self, self.Items.GetRange(i, size)).Inner));
		return Kv.List<KList<T>>(res);
	}

	/// <summary>(подходящие, остальные) по предикату.</summary>
	[KernMember] public static KTuple<KList<T>, KList<T>> Partition<T>(KList<T> self, KFunc<T, bool> predicate)
	{
		var yes = new List<KValue>(); var no = new List<KValue>();
		foreach (var x in self.Items.ToArray()) (Kv.Truthy(predicate.Invoke(x)) ? yes : no).Add(x);
		return new(Kv.Tuple(KValue.Obj(Same(self, yes).Inner), KValue.Obj(Same(self, no).Inner)));
	}

	[KernMember] public static KList<KTuple<T, U>> Zip<T, U>(KList<T> self, KList<U> other) =>
		Kv.List<KTuple<T, U>>(self.Items.Zip(other.Items, (a, b) => KValue.Obj(Kv.Tuple(a, b))));

	[KernMember] public static KList<T> Concat<T>(KList<T> self, KList<T> other) => Same(self, self.Items.Concat(other.Items));
}
