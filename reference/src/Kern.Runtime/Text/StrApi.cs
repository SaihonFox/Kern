using System.Globalization;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Values;

namespace Kern.Runtime.Text;

/// <summary>Члены встроенного типа str: свойства, преобразования, форматирование. Поиск — в StrApi.Search, разбиение — в StrApi.Split.</summary>
[KernType("str")]
public static partial class StrApi
{
	// ---------- свойства ----------
	[KernProperty] public static int Len(string self) => self.Length;
	[KernProperty] public static bool IsEmpty(string self) => self.Length == 0;
	[KernProperty] public static bool IsBlank(string self) => string.IsNullOrWhiteSpace(self);
	[KernProperty] public static string Uppercase(string self) => self.ToUpperInvariant();
	[KernProperty] public static string Lowercase(string self) => self.ToLowerInvariant();
	[KernProperty] public static string Trim(string self) => self.Trim();
	[KernProperty] public static string TrimStart(string self) => self.TrimStart();
	[KernProperty] public static string TrimEnd(string self) => self.TrimEnd();
	[KernProperty] public static string Reversed(string self) { var a = self.ToCharArray(); Array.Reverse(a); return new string(a); }
	[KernProperty] public static KList<char> Chars(string self) => Kv.Chars(self);
	[KernProperty] public static string Capitalized(string self) => self.Length == 0 ? self : char.ToUpperInvariant(self[0]) + self[1..];

	// ---------- базовые методы ----------
	[KernMember] public static string ToStr(string self) => self;
	[KernMember] public static string Trim(string self, string chars) => self.Trim(chars.ToCharArray());
	[KernMember] public static string TrimStart(string self, string chars) => self.TrimStart(chars.ToCharArray());
	[KernMember] public static string TrimEnd(string self, string chars) => self.TrimEnd(chars.ToCharArray());
	[KernMember] public static string Replace(string self, string from, string to) => from.Length == 0 ? self : self.Replace(from, to, StringComparison.Ordinal);
	[KernMember] public static string Replace(string self, char from, char to) => self.Replace(from, to);
	[KernMember] public static string ReplaceFirst(string self, string from, string to)
	{
		int i = self.IndexOf(from, StringComparison.Ordinal);
		return i < 0 ? self : string.Concat(self.AsSpan(0, i), to, self.AsSpan(i + from.Length));
	}
	[KernMember] public static string Substring(string self, int start, int length = -1)
	{
		if (start < 0 || start > self.Length) throw new ArgumentOutOfRangeException(nameof(start), $"start={start}, len={self.Length}");
		return length < 0 ? self[start..] : self.Substring(start, System.Math.Min(length, self.Length - start));
	}

	/// <summary>Срез [start, end) с поддержкой отрицательных индексов (отсчёт с конца); end по умолчанию — конец строки.</summary>
	[KernMember] public static string Slice(string self, int start, int? end = null)
	{
		int n = self.Length;
		int s = System.Math.Clamp(start < 0 ? n + start : start, 0, n);
		int e = System.Math.Clamp(end is { } x ? (x < 0 ? n + x : x) : n, 0, n);
		return e <= s ? "" : self[s..e];
	}

	[KernMember] public static string Take(string self, int count) => self[..System.Math.Clamp(count, 0, self.Length)];
	[KernMember] public static string Skip(string self, int count) => self[System.Math.Clamp(count, 0, self.Length)..];
	[KernMember] public static string Repeat(string self, int count) => count <= 0 ? "" : string.Concat(Enumerable.Repeat(self, count));
	[KernMember] public static string PadLeft(string self, int width, char fill = ' ') => self.PadLeft(width, fill);
	[KernMember] public static string PadRight(string self, int width, char fill = ' ') => self.PadRight(width, fill);
	[KernMember] public static string Center(string self, int width, char fill = ' ')
	{
		int total = width - self.Length;
		if (total <= 0) return self;
		int left = total / 2;
		return new string(fill, left) + self + new string(fill, total - left);
	}
	[KernMember] public static string Insert(string self, int index, string value) => self.Insert(index, value);
	[KernMember] public static string Remove(string self, int start, int count = -1) => count < 0 ? self.Remove(start) : self.Remove(start, count);
	[KernMember] public static string Concat(string self, string other) => self + other;
	[KernMember] public static string Truncate(string self, int max, string ellipsis = "…") =>
		self.Length <= max ? self : self[..System.Math.Max(0, max - ellipsis.Length)] + ellipsis;
	[KernMember] public static string Indent(string self, int spaces) =>
		string.Join('\n', self.Split('\n').Select(l => l.Length == 0 ? l : new string(' ', spaces) + l));
	[KernMember] public static string Normalize(string self) => self.Normalize();

	// ---------- сравнение ----------
	[KernMember] public static bool EqualsIgnoreCase(string self, string other) => string.Equals(self, other, StringComparison.OrdinalIgnoreCase);
	[KernMember] public static int CompareTo(string self, string other) => System.Math.Sign(string.CompareOrdinal(self, other));
	[KernMember] public static int CompareIgnoreCase(string self, string other) => System.Math.Sign(string.Compare(self, other, StringComparison.OrdinalIgnoreCase));
	[KernMember("hash_code")] public static int HashOf(string self) => self.GetHashCode();

	// ---------- преобразования (исключение = panic) ----------
	[KernMember] public static int ToI32(string self) => int.Parse(self.Trim(), CultureInfo.InvariantCulture);
	[KernMember] public static long ToI64(string self) => long.Parse(self.Trim(), CultureInfo.InvariantCulture);
	[KernMember] public static ulong ToU64(string self) => ulong.Parse(self.Trim(), CultureInfo.InvariantCulture);
	[KernMember] public static double ToF64(string self) => double.Parse(self.Trim(), CultureInfo.InvariantCulture);
	[KernMember] public static bool ToBool(string self) => bool.Parse(self.Trim());
	[KernMember] public static int ToI32(string self, int radix) => Convert.ToInt32(self.Trim(), radix);
	[KernMember] public static long ToI64(string self, int radix) => Convert.ToInt64(self.Trim(), radix);

	// ---------- безопасные преобразования: result / nil ----------
	[KernMember] public static KResult<int, string> ToI32OrErr(string self) =>
		int.TryParse(self.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
			? Kv.Ok<int>(V(v)) : Kv.Err<int>($"не число i32: \"{self}\"");
	[KernMember] public static KResult<long, string> ToI64OrErr(string self) =>
		long.TryParse(self.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
			? Ok<long>(V(v)) : Kv.Err<long>($"не число i64: \"{self}\"");
	[KernMember] public static KResult<double, string> ToF64OrErr(string self) =>
		double.TryParse(self.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
			? Ok<double>(V(v)) : Kv.Err<double>($"не число f64: \"{self}\"");
	[KernMember] public static KResult<bool, string> ToBoolOrErr(string self) =>
		bool.TryParse(self.Trim(), out var v) ? Ok<bool>(KValue.Bool(v)) : Kv.Err<bool>($"не bool: \"{self}\"");

	[KernMember] public static int? ToI32OrNil(string self) => int.TryParse(self.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
	[KernMember] public static long? ToI64OrNil(string self) => long.TryParse(self.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
	[KernMember] public static double? ToF64OrNil(string self) => double.TryParse(self.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

	private static KValue V(int v) => KValue.I32(v);
	private static KValue V(long v) => KValue.I64(v);
	private static KValue V(double v) => KValue.F64(v);
	private static KResult<T, string> Ok<T>(KValue v) => Kv.Ok<T>(v);
}
