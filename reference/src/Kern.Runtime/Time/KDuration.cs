using System.Globalization;

namespace Kern.Runtime.Time;

/// <summary>Промежуток времени (значение, неизменяемое). В Kern — тип <c>duration</c>; хранится в KNativeBox.</summary>
public readonly struct KDuration(TimeSpan span) : IEquatable<KDuration>, IComparable<KDuration>
{
	public TimeSpan Span { get; } = span;

	public bool Equals(KDuration other) => Span == other.Span;
	public override bool Equals(object? obj) => obj is KDuration d && Equals(d);
	public override int GetHashCode() => Span.GetHashCode();
	public int CompareTo(KDuration other) => Span.CompareTo(other.Span);

	/// <summary>`250ms`, `1.5s`, `2m 3s`, `1h 2m 3s`, `2d 3h`.</summary>
	public override string ToString()
	{
		var t = Span;
		string sign = t < TimeSpan.Zero ? "-" : "";
		if (t < TimeSpan.Zero) t = t.Negate();
		var ci = CultureInfo.InvariantCulture;
		if (t < TimeSpan.FromSeconds(1)) return sign + t.TotalMilliseconds.ToString("0.###", ci) + "ms";
		if (t < TimeSpan.FromMinutes(1)) return sign + t.TotalSeconds.ToString("0.###", ci) + "s";
		var parts = new List<string>();
		if (t.Days > 0) parts.Add(t.Days + "d");
		if (t.Hours > 0) parts.Add(t.Hours + "h");
		if (t.Minutes > 0) parts.Add(t.Minutes + "m");
		if (t.Seconds > 0 || t.Milliseconds > 0)
			parts.Add((t.Seconds + t.Milliseconds / 1000.0).ToString("0.###", ci) + "s");
		return sign + string.Join(' ', parts);
	}
}
