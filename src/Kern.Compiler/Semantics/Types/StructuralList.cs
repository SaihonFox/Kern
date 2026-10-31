using System.Collections;

namespace Kern.Compiler.Semantics.Types;

/// <summary>
/// Неизменяемый список с поэлементным равенством. Записи (records) сравнивают списки по ссылке,
/// поэтому все коллекции внутри <see cref="KernType"/> хранятся в этой обёртке.
/// </summary>
internal sealed class StructuralList<T> : IReadOnlyList<T>, IEquatable<StructuralList<T>>
{
	private readonly T[] _items;

	public StructuralList(IEnumerable<T> items) => _items = [.. items];

	public static StructuralList<T> Empty { get; } = new([]);

	public static StructuralList<T> From(IReadOnlyList<T> items) =>
		items as StructuralList<T> ?? new StructuralList<T>(items);

	public T this[int index] => _items[index];

	public int Count => _items.Length;

	public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

	public bool Equals(StructuralList<T>? other) =>
		other is not null && _items.AsSpan().SequenceEqual(other._items);

	public override bool Equals(object? obj) => Equals(obj as StructuralList<T>);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		foreach (var item in _items)
			hash.Add(item);
		return hash.ToHashCode();
	}
}
