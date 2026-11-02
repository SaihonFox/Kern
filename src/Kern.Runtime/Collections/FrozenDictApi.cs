using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections;

/// <summary>
/// Неизменяемый словарь <c>frozen_dict&lt;K, V&gt;</c> (spec §16): копия пар обычного
/// <c>dict&lt;K, V&gt;</c> в порядке источника. Изменения источника на копию не влияют.
/// </summary>
[KernType("frozen_dict", GenericArity = 2)]
public sealed class KernFrozenDict : IKernCollection
{
	private readonly KValue[] _keys;
	private readonly KValue[] _values;

	/// <summary>Скопировать пары словаря.</summary>
	public KernFrozenDict(KValue source)
	{
		var keys = new List<KValue>();
		var values = new List<KValue>();
		foreach (var pair in CollectionOps.ToDict(source).Items)
		{
			keys.Add(pair.Key);
			values.Add(pair.Value);
		}

		_keys = [.. keys];
		_values = [.. values];
	}

	/// <summary>Число пар.</summary>
	public int Count => _keys.Length;

	/// <summary>Ключи в порядке источника.</summary>
	public IEnumerable<KValue> Items => _keys;

	/// <summary>Есть ли ключ.</summary>
	public bool Contains(KValue item) => ContainsKey(item);

	/// <summary>Есть ли ключ.</summary>
	public bool ContainsKey(KValue key) => IndexOf(key) >= 0;

	/// <summary>Значение по ключу; отсутствие ключа — паника.</summary>
	public KValue Get(KValue key)
	{
		int index = IndexOf(key);
		if (index < 0)
			throw new KernPanic($"frozen_dict: ключ {key} отсутствует");
		return _values[index];
	}

	/// <summary>Значение по ключу или запасное значение.</summary>
	public KValue GetOr(KValue key, KValue fallback)
	{
		int index = IndexOf(key);
		return index < 0 ? fallback : _values[index];
	}

	/// <summary>Ключи словаря.</summary>
	public KList Keys() => new(null, _keys);

	/// <summary>Значения в порядке ключей.</summary>
	public KList Values() => new(null, _values);

	/// <summary>Общий протокол коллекции: <c>to_list</c> словаря — его ключи.</summary>
	public KList ToList() => Keys();

	private int IndexOf(KValue key)
	{
		for (var index = 0; index < _keys.Length; index++)
		{
			if (_keys[index].Equals(key))
				return index;
		}

		return -1;
	}

	public override string ToString() => $"frozen_dict({_keys.Length})";
}

/// <summary>
/// Фабрика и члены <c>frozen_dict&lt;K, V&gt;</c> (spec §16): <c>frozen_dict(dict)</c>,
/// свойства <c>len</c>/<c>is_empty</c>, <c>get/get_or/contains_key/keys/values/to_list</c>.
/// </summary>
[KernType("frozen_dict", GenericArity = 2)]
public static class FrozenDictApi
{
	/// <summary><c>frozen_dict(items)</c>: неизменяемая копия словаря.</summary>
	[KernCtor]
	[KernName("frozen_dict")]
	[KernReturn("frozen_dict<T1,T2>")]
	public static KValue Create<T1, T2>([KernParameterType("dict<T1,T2>")] KValue items) =>
		KValue.Obj(new KNativeBox(new KernFrozenDict(items)));

	/// <summary><c>len</c>: число пар.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).Count;

	/// <summary><c>is_empty</c>: словарь пуст.</summary>
	[KernName("is_empty")]
	[KernProperty]
	public static bool IsEmpty(KNativeBox self) => Require(self).Count == 0;

	/// <summary><c>get(k) -&gt; V</c>: значение по ключу.</summary>
	[KernMember]
	[KernReturn("T2")]
	public static KValue Get(KNativeBox self, KValue key) => Require(self).Get(key);

	/// <summary><c>get_or(k, fallback) -&gt; V</c>: значение или запасное.</summary>
	[KernName("get_or")]
	[KernMember]
	[KernReturn("T2")]
	public static KValue GetOr(KNativeBox self, KValue key, KValue fallback) =>
		Require(self).GetOr(key, fallback);

	/// <summary><c>contains_key(k)</c>: есть ли ключ.</summary>
	[KernName("contains_key")]
	[KernMember]
	public static bool ContainsKey(KNativeBox self, KValue key) => Require(self).ContainsKey(key);

	/// <summary><c>keys() -&gt; list&lt;K&gt;</c>: ключи.</summary>
	[KernName("keys")]
	[KernMember]
	[KernReturn("list<T1>")]
	public static KValue Keys(KNativeBox self) => KValue.Obj(Require(self).Keys());

	/// <summary><c>keys</c> без скобок — свойство.</summary>
	[KernName("keys")]
	[KernProperty]
	public static KValue KeysProperty(KNativeBox self) => Keys(self);

	/// <summary><c>values() -&gt; list&lt;V&gt;</c>: значения.</summary>
	[KernName("values")]
	[KernMember]
	[KernReturn("list<T2>")]
	public static KValue Values(KNativeBox self) => KValue.Obj(Require(self).Values());

	/// <summary><c>values</c> без скобок — свойство.</summary>
	[KernName("values")]
	[KernProperty]
	public static KValue ValuesProperty(KNativeBox self) => Values(self);

	/// <summary><c>to_list() -&gt; list&lt;K&gt;</c>: ключи (протокол коллекции).</summary>
	[KernName("to_list")]
	[KernMember]
	[KernReturn("list<T1>")]
	public static KValue ToList(KNativeBox self) => KValue.Obj(Require(self).ToList());

	/// <summary><c>contains(k)</c>: есть ли ключ (для <c>in</c>).</summary>
	[KernMember]
	public static bool Contains(KNativeBox self, KValue key) => Require(self).ContainsKey(key);

	private static KernFrozenDict Require(KNativeBox self) => self.Value switch
	{
		KernFrozenDict dict => dict,
		_ => throw new KernPanic($"ожидался frozen_dict, получено {self.Value.GetType().Name}"),
	};
}
