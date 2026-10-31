using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Collections.Containers;

/// <summary>Фабрика адаптеров для `collection&lt;T&gt;`: list / array / str / dict / range → <see cref="KernCollectionAdapter"/>.</summary>
public sealed class KernCollectionAdapterFactory : IValueAdapterFactory
{
	public object? Adapt(KValue value) =>
		value.Ref is KListObject or KArrayObject or KDictObject or KRange || value.Kind == ValueKind.Str
			? new KernCollectionAdapter(value)
			: null;
}

/// <summary>
/// Представляет встроенную коллекцию VM как `collection&lt;T&gt;`. Изменяемые операции (clear) работают у list и dict,
/// у array / str / range — паника (они неизменяемы по размеру).
/// dict обходится парами `(key, value)`.
/// </summary>
public sealed class KernCollectionAdapter(KValue value) : KernCollection<KValue>, IValueAdapter
{
	public KValue Original { get; } = value;

	public override int Len => Original.Ref switch
	{
		KListObject l => l.Items.Count,
		KArrayObject a => a.Items.Length,
		KDictObject d => d.Items.Count,
		KRange r => RangeApi.Values(r).Count(),
		_ => Original.AsStr.Length,
	};

	public override void Clear()
	{
		switch (Original.Ref)
		{
			case KListObject l: l.Items.Clear(); break;
			case KDictObject d: d.Items.Clear(); break;
			default: throw new KernPanic("clear: коллекция неизменяема");
		}
	}

	private IEnumerable<KValue> Items() => Original.Ref switch
	{
		KListObject l => l.Items,
		KArrayObject a => a.Items,
		KDictObject d => d.Items.Select(kv => KValue.Obj(Kv.Tuple(kv.Key, kv.Value))),
		KRange r => RangeApi.Values(r).Select(KValue.I64),
		_ => Original.AsStr.Select(KValue.Char),
	};

	public override KList<KValue> Iter() => Kv.List<KValue>(Items());
}
