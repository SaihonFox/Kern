using System.Globalization;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Time;

/// <summary>
/// Дата и время со смещением от UTC: <c>datetime.now()</c>, <c>datetime.of(2026, 10, 14)</c>, <c>dt.year</c>, <c>dt.add_days(3)</c>,
/// <c>dt2 - dt1</c> (duration), <c>dt + duration.from_hours(2)</c>, <c>dt.format("yyyy-MM-dd")</c>.
/// Первый метод с <c>self</c> обязан быть над <see cref="KDateTime"/> — по нему тип регистрируется в каталоге.
/// </summary>
[KernType("datetime")]
public static class DateTimeApi
{
	// ---------- создание ----------
	/// <summary>Текущий момент в местном часовом поясе.</summary>
	[KernStatic] public static KDateTime Now() => new(DateTimeOffset.Now);
	[KernStatic] public static KDateTime UtcNow() => new(DateTimeOffset.UtcNow);
	/// <summary>Сегодня, 00:00 по местному времени.</summary>
	[KernStatic] public static KDateTime Today() => new(new DateTimeOffset(DateTime.Today));
	/// <summary>Дата (UTC, 00:00:00).</summary>
	[KernStatic] public static KDateTime Of(int year, int month, int day) => Make(year, month, day, 0, 0, 0);
	/// <summary>Дата и время (UTC).</summary>
	[KernStatic] public static KDateTime Of(int year, int month, int day, int hour, int minute, int second) => Make(year, month, day, hour, minute, second);
	[KernStatic] public static KDateTime FromUnix(long seconds) => new(DateTimeOffset.FromUnixTimeSeconds(seconds));
	[KernStatic] public static KDateTime FromUnixMs(long milliseconds) => new(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));

	[KernStatic] public static KResult<KDateTime, string> TryParse(string text) =>
		DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
			? Kv.Ok<KDateTime>(KValue.Obj(new KNativeBox(new KDateTime(d)))) : Kv.Err<KDateTime>($"не datetime: \"{text}\"");

	[KernStatic] public static KDateTime Parse(string text) =>
		DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
			? new KDateTime(d) : throw new KernPanic($"не datetime: \"{text}\"");

	[KernStatic] public static int DaysInMonth(int year, int month) => DateTime.DaysInMonth(year, month);
	[KernStatic] public static bool IsLeapYear(int year) => DateTime.IsLeapYear(year);

	private static KDateTime Make(int y, int mo, int d, int h, int mi, int s)
	{
		try { return new KDateTime(new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.Zero)); }
		catch (ArgumentOutOfRangeException) { throw new KernPanic($"некорректная дата/время: {y}-{mo}-{d} {h}:{mi}:{s}"); }
	}

	// ---------- компоненты ----------
	[KernProperty] public static int Year(KDateTime self) => self.Value.Year;
	[KernProperty] public static int Month(KDateTime self) => self.Value.Month;
	[KernProperty] public static int Day(KDateTime self) => self.Value.Day;
	[KernProperty] public static int Hour(KDateTime self) => self.Value.Hour;
	[KernProperty] public static int Minute(KDateTime self) => self.Value.Minute;
	[KernProperty] public static int Second(KDateTime self) => self.Value.Second;
	[KernProperty] public static int Ms(KDateTime self) => self.Value.Millisecond;
	/// <summary>День недели по ISO: 1 — понедельник … 7 — воскресенье.</summary>
	[KernProperty] public static int Weekday(KDateTime self) => self.Value.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)self.Value.DayOfWeek;
	[KernProperty] public static int DayOfYear(KDateTime self) => self.Value.DayOfYear;
	[KernProperty] public static long Unix(KDateTime self) => self.Value.ToUnixTimeSeconds();
	[KernProperty] public static long UnixMs(KDateTime self) => self.Value.ToUnixTimeMilliseconds();
	/// <summary>Смещение от UTC.</summary>
	[KernProperty] public static KDuration Offset(KDateTime self) => new(self.Value.Offset);
	[KernProperty] public static bool IsUtc(KDateTime self) => self.Value.Offset == TimeSpan.Zero;
	/// <summary>Та же дата, 00:00:00 (с тем же смещением).</summary>
	[KernProperty] public static KDateTime Date(KDateTime self) => new(new DateTimeOffset(self.Value.Date, self.Value.Offset));
	/// <summary>Время суток как промежуток от полуночи.</summary>
	[KernProperty] public static KDuration TimeOfDay(KDateTime self) => new(self.Value.TimeOfDay);

	// ---------- арифметика ----------
	[KernMember] public static KDateTime Add(KDateTime self, KDuration d) => new(self.Value + d.Span);
	[KernMember] public static KDateTime AddDays(KDateTime self, double days) => new(self.Value.AddDays(days));
	[KernMember] public static KDateTime AddHours(KDateTime self, double hours) => new(self.Value.AddHours(hours));
	[KernMember] public static KDateTime AddMinutes(KDateTime self, double minutes) => new(self.Value.AddMinutes(minutes));
	[KernMember] public static KDateTime AddSeconds(KDateTime self, double seconds) => new(self.Value.AddSeconds(seconds));
	[KernMember] public static KDateTime AddMs(KDateTime self, double milliseconds) => new(self.Value.AddMilliseconds(milliseconds));
	[KernMember] public static KDateTime AddMonths(KDateTime self, int months) => new(self.Value.AddMonths(months));
	[KernMember] public static KDateTime AddYears(KDateTime self, int years) => new(self.Value.AddYears(years));

	[KernOperator("+")] public static KDateTime Plus(KDateTime self, KDuration d) => new(self.Value + d.Span);
	[KernOperator("-")] public static KDateTime Minus(KDateTime self, KDuration d) => new(self.Value - d.Span);
	[KernOperator("-")] public static KDuration Between(KDateTime self, KDateTime other) => new(self.Value - other.Value);
	[KernOperator("==")] public static bool Eq(KDateTime self, KDateTime other) => self.Value.Equals(other.Value);
	[KernOperator("<")] public static bool Lt(KDateTime self, KDateTime other) => self.Value < other.Value;
	[KernOperator("<=>")] public static int Cmp(KDateTime self, KDateTime other) => self.Value.CompareTo(other.Value);

	// ---------- часовые пояса ----------
	[KernMember] public static KDateTime ToUtc(KDateTime self) => new(self.Value.ToUniversalTime());
	[KernMember] public static KDateTime ToLocal(KDateTime self) => new(self.Value.ToLocalTime());
	/// <summary>Тот же момент, показанный со смещением в часах (например, 3 или 5.5).</summary>
	[KernMember] public static KDateTime WithOffsetHours(KDateTime self, double hours) => new(self.Value.ToOffset(TimeSpan.FromHours(hours)));

	// ---------- текст ----------
	/// <summary>Форматирование по .NET-шаблону: <c>"yyyy-MM-dd HH:mm:ss"</c>, <c>"dd.MM.yyyy"</c>.</summary>
	[KernMember] public static string Format(KDateTime self, string pattern) => self.Value.ToString(pattern, CultureInfo.InvariantCulture);
	[KernMember] public static string ToStr(KDateTime self) => self.ToString();
	[KernMember] public static int CompareTo(KDateTime self, KDateTime other) => self.Value.CompareTo(other.Value);
}
