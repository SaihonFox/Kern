using Kern.Vm.Types;

namespace Kern.Vm.Values;

/// <summary>Экземпляр пользовательского type/unit. Поля — по слотам.</summary>
public sealed class KObject(KClassType type, KValue[] fields)
{
	public KClassType Type { get; } = type;
	public KValue[] Fields { get; } = fields;
	/// <summary>Нативная часть (если тип наследует нативный класс или это обёртка).</summary>
	public object? Native { get; set; }
	public KObject CopyUnit() => new(Type, (KValue[])Fields.Clone()) { Native = Native };
}

public sealed class KListObject(KType elementType, List<KValue>? items = null)
{
	public KType ElementType { get; } = elementType;
	public List<KValue> Items { get; } = items ?? [];
}

public sealed class KArrayObject(KType elementType, KValue[] items)
{
	public KType ElementType { get; } = elementType;
	public KValue[] Items { get; } = items;
}

public sealed class KDictObject(KType keyType, KType valueType)
{
	public KType KeyType { get; } = keyType;
	public KType ValueType { get; } = valueType;
	public Dictionary<KValue, KValue> Items { get; } = new(KValueComparer.Instance);
}

public sealed class KTupleObject(KValue[] items, string?[]? names = null)
{
	public KValue[] Items { get; } = items;
	public string?[]? Names { get; } = names;
}

/// <summary>Результат Ok/Err.</summary>
public sealed class KResultObject(bool isOk, KValue value)
{
	public bool IsOk { get; } = isOk;
	public KValue Value { get; } = value;
}

/// <summary>Любой .NET-объект, пришедший из `use dotnet`/нативного типа.</summary>
public sealed class KNativeBox(object value)
{
	public object Value { get; } = value;
}

/// <summary>Значение typeof(...)</summary>
public sealed class KTypeObject(KType type)
{
	public KType Type { get; } = type;
}

public sealed class KValueComparer : IEqualityComparer<KValue>
{
	public static readonly KValueComparer Instance = new();
	public bool Equals(KValue a, KValue b) => ValueOps.StructuralEquals(a, b);
	public int GetHashCode(KValue v) => ValueOps.StructuralHash(v);
}
