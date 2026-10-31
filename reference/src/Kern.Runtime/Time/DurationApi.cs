using System.Globalization;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Time;

/// <summary>
/// Промежуток времени: <c>duration.from_seconds(1.5)</c>, <c>d.total_ms</c>, <c>d1 + d2</c>, <c>d * 2</c>, сравнения.
/// Первый метод с <c>self</c> обязан быть над <see cref="KDuration"/> — по нему тип регистрируется в каталоге.
/// </summary>
[KernType("duration")]
public static class DurationApi
{
	// ---------- создание ----------
	[KernStatic] public static KDuration FromMs(double value) => new(TimeSpan.FromMilliseconds(value));
	[KernStatic] public static KDuration FromSeconds(double value) => new(TimeSpan.FromSeconds(value));
	[KernStatic] public static KDuration FromMinutes(double value) => new(TimeSpan.FromMinutes(value));
	[KernStatic] public static KDuration FromHours(double value) => new(TimeSpan.FromHours(value));
	[KernStatic] public static KDuration FromDays(double value) => new(TimeSpan.FromDays(value));
	[KernStatic] public static KDuration Zero() => new(TimeSpan.Zero);

	/// <summary>Разбор `250ms`, `1.5s`, `2m`, `1h30m`, `1d 2h`; ошибка — результат Err.</summary>
	[KernStatic] public static KResult<KDuration, string> TryParse(string text)
	{
		if (TryParseCore(text, out var d)) return Kv.Ok<KDuration>(KValue.Obj(new KNativeBox(d)));
		return Kv.Err<KDuration>($"не duration: \"{text}\" (ожидается, например, 250ms, 1.5s, 2m, 1h30m)");
	}

	[KernStatic] public static KDuration Parse(string text) =>
		TryParseCore(text, out var d) ? d : throw new KernPanic($"не duration: \"{text}\"");

	private static bool TryParseCore(string text, out KDuration result)
	{
		result = default;
		var s = text.Trim();
		if (s.Length == 0) return false;
		bool negative = s[0] == '-';
		if (negative) s = s[1..].TrimStart();
		double totalMs = 0;
		int i = 0;
		bool any = false;
		while (i < s.Length)
		{
			while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
			int start = i;
			while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
			if (start == i || !double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return false;
			int us = i;
			while (i < s.Length && char.IsLetter(s[i])) i++;
			double factor = s[us..i] switch
			{
				"ms" => 1, "s" => 1000, "m" => 60_000, "h" => 3_600_000, "d" => 86_400_000, _ => -1,
			};
			if (factor < 0) return false;
			totalMs += num * factor;
			any = true;
		}
		if (!any) return false;
		result = new KDuration(TimeSpan.FromMilliseconds(negative ? -totalMs : totalMs));
		return true;
	}

	// ---------- общее время ----------
	[KernProperty] public static double TotalMs(KDuration self) => self.Span.TotalMilliseconds;
	[KernProperty] public static double TotalSeconds(KDuration self) => self.Span.TotalSeconds;
	[KernProperty] public static double TotalMinutes(KDuration self) => self.Span.TotalMinutes;
	[KernProperty] public static double TotalHours(KDuration self) => self.Span.TotalHours;
	[KernProperty] public static double TotalDays(KDuration self) => self.Span.TotalDays;

	// ---------- компоненты ----------
	[KernProperty] public static int Ms(KDuration self) => self.Span.Milliseconds;
	[KernProperty] public static int Seconds(KDuration self) => self.Span.Seconds;
	[KernProperty] public static int Minutes(KDuration self) => self.Span.Minutes;
	[KernProperty] public static int Hours(KDuration self) => self.Span.Hours;
	[KernProperty] public static int Days(KDuration self) => self.Span.Days;

	[KernProperty] public static bool IsNegative(KDuration self) => self.Span < TimeSpan.Zero;
	[KernProperty] public static KDuration Abs(KDuration self) => new(self.Span.Duration());

	// ---------- операторы ----------
	[KernOperator("+")] public static KDuration Add(KDuration self, KDuration other) => new(self.Span + other.Span);
	[KernOperator("-")] public static KDuration Sub(KDuration self, KDuration other) => new(self.Span - other.Span);
	[KernOperator("-")] public static KDuration Neg(KDuration self) => new(self.Span.Negate());
	[KernOperator("*")] public static KDuration Mul(KDuration self, double factor) => new(self.Span * factor);
	[KernOperator("/")] public static KDuration Div(KDuration self, double divisor) => new(self.Span / divisor);
	[KernOperator("/")] public static double Ratio(KDuration self, KDuration other) => self.Span / other.Span;
	[KernOperator("==")] public static bool Eq(KDuration self, KDuration other) => self.Span == other.Span;
	[KernOperator("<")] public static bool Lt(KDuration self, KDuration other) => self.Span < other.Span;
	[KernOperator("<=>")] public static int Cmp(KDuration self, KDuration other) => self.Span.CompareTo(other.Span);

	// ---------- методы ----------
	[KernMember] public static string ToStr(KDuration self) => self.ToString();
	[KernMember] public static int CompareTo(KDuration self, KDuration other) => self.Span.CompareTo(other.Span);
	[KernMember] public static KDuration Min(KDuration self, KDuration other) => self.Span <= other.Span ? self : other;
	[KernMember] public static KDuration Max(KDuration self, KDuration other) => self.Span >= other.Span ? self : other;
	/// <summary>Блокирующий сон на этот промежуток. В async-коде используйте <c>await delay(ms)</c>.</summary>
	[KernMember] public static void Sleep(KDuration self) { if (self.Span > TimeSpan.Zero) Thread.Sleep(self.Span); }
}
