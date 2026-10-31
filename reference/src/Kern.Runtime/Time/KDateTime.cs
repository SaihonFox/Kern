using System.Globalization;

namespace Kern.Runtime.Time;

/// <summary>Момент времени со смещением от UTC (значение, неизменяемое). В Kern — тип <c>datetime</c>; хранится в KNativeBox.</summary>
public readonly struct KDateTime(DateTimeOffset value) : IEquatable<KDateTime>, IComparable<KDateTime>
{
	public DateTimeOffset Value { get; } = value;

	public bool Equals(KDateTime other) => Value.Equals(other.Value);
	public override bool Equals(object? obj) => obj is KDateTime d && Equals(d);
	public override int GetHashCode() => Value.GetHashCode();
	public int CompareTo(KDateTime other) => Value.CompareTo(other.Value);

	/// <summary>ISO 8601: `2026-10-14T12:30:00.000Z` для UTC, иначе со смещением `+03:00`.</summary>
	public override string ToString() => Value.Offset == TimeSpan.Zero
		? Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
		: Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
}
