using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>Члены dict&lt;K, V&gt;. Ключи сравниваются структурно (KValueComparer).</summary>
[KernType("dict")]
public static class DictApi
{
	private static KernPanic MissingKey(KValue key) => new($"в словаре нет ключа {ValueOps.Repr(key)}");

	// ---------- свойства ----------
	[KernProperty] public static int Len<K, V>(KDict<K, V> self) => self.Items.Count;
	[KernProperty] public static bool IsEmpty<K, V>(KDict<K, V> self) => self.Items.Count == 0;
	[KernProperty] public static KList<K> Keys<K, V>(KDict<K, V> self) => Kv.List<K>(self.Inner.KeyType, self.Items.Keys);
	[KernProperty] public static KList<V> Values<K, V>(KDict<K, V> self) => Kv.List<V>(self.Inner.ValueType, self.Items.Values);
	[KernProperty] public static KList<KTuple<K, V>> Entries<K, V>(KDict<K, V> self) =>
		Kv.List<KTuple<K, V>>(self.Items.Select(p => KValue.Obj(Kv.Tuple(p.Key, p.Value))));

	// ---------- доступ ----------
	[KernMember] public static V Get<K, V>(KDict<K, V> self, K key) =>
		self.Items.TryGetValue(Kv.Of(key), out var v) ? Kv.As<V>(v) : throw MissingKey(Kv.Of(key));

	[KernMember] public static V? GetOrNil<K, V>(KDict<K, V> self, K key) =>
		Kv.As<V>(self.Items.TryGetValue(Kv.Of(key), out var v) ? v : KValue.Nil);

	[KernMember] public static V GetOr<K, V>(KDict<K, V> self, K key, V fallback) =>
		self.Items.TryGetValue(Kv.Of(key), out var v) ? Kv.As<V>(v) : fallback;

	/// <summary>Возвращает значение по ключу; если его нет — вычисляет, сохраняет и возвращает.</summary>
	[KernMember] public static V GetOrAdd<K, V>(KDict<K, V> self, K key, KFunc<V> factory)
	{
		if (self.Items.TryGetValue(Kv.Of(key), out var v)) return Kv.As<V>(v);
		var created = factory.Invoke();
		self.Items[Kv.Of(key)] = created;
		return Kv.As<V>(created);
	}

	[KernMember] public static void Set<K, V>(KDict<K, V> self, K key, V value) => self.Items[Kv.Of(key)] = Kv.Of(value);

	/// <summary>true, если ключ добавлен; false, если уже был (значение не меняется).</summary>
	[KernMember] public static bool TryAdd<K, V>(KDict<K, V> self, K key, V value) => self.Items.TryAdd(Kv.Of(key), Kv.Of(value));

	[KernMember] public static bool ContainsKey<K, V>(KDict<K, V> self, K key) => self.Items.ContainsKey(Kv.Of(key));

	[KernMember] public static bool ContainsValue<K, V>(KDict<K, V> self, V value)
	{
		var target = Kv.Of(value);
		return self.Items.Values.Any(v => Kv.Eq(v, target));
	}

	[KernMember] public static bool Remove<K, V>(KDict<K, V> self, K key) => self.Items.Remove(Kv.Of(key));
	[KernMember] public static void Clear<K, V>(KDict<K, V> self) => self.Items.Clear();

	// ---------- функциональные операции ----------
	[KernMember] public static KDict<K, V> Copy<K, V>(KDict<K, V> self)
	{
		var d = new KDictObject(self.Inner.KeyType, self.Inner.ValueType);
		foreach (var (k, v) in self.Items) d.Items[k] = v;
		return new KDict<K, V>(d);
	}

	/// <summary>Добавляет все пары из other (существующие ключи перезаписываются).</summary>
	[KernMember] public static KDict<K, V> Merge<K, V>(KDict<K, V> self, KDict<K, V> other)
	{
		foreach (var (k, v) in other.Items) self.Items[k] = v;
		return self;
	}

	[KernMember] public static KDict<K, V> Filter<K, V>(KDict<K, V> self, KFunc<K, V, bool> predicate)
	{
		var d = new KDictObject(self.Inner.KeyType, self.Inner.ValueType);
		foreach (var (k, v) in self.Items.ToArray())
			if (Kv.Truthy(predicate.Invoke(k, v))) d.Items[k] = v;
		return new KDict<K, V>(d);
	}

	[KernMember] public static KDict<K, R> MapValues<K, V, R>(KDict<K, V> self, KFunc<V, R> f, [KernTypeArgs] KType[] typeArgs)
	{
		var d = new KDictObject(self.Inner.KeyType, Kv.Last(typeArgs));
		foreach (var (k, v) in self.Items.ToArray()) d.Items[k] = f.Invoke(v);
		return new KDict<K, R>(d);
	}

	[KernMember] public static void Each<K, V>(KDict<K, V> self, KAction<K, V> action)
	{
		foreach (var (k, v) in self.Items.ToArray()) action.Invoke(k, v);
	}

	[KernMember] public static string ToStr<K, V>(KDict<K, V> self) => ValueOps.ToStr(KValue.Obj(self.Inner));
}
