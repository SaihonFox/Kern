using System.Diagnostics;
using System.Globalization;
using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Time;

/// <summary>Длительность (<c>duration</c>, spec §18c): хранится в миллисекундах.</summary>
[KernType("duration")]
public sealed class KernDuration(long milliseconds)
{
	public long Milliseconds { get; } = milliseconds;

	public override string ToString()
	{
		long total = Milliseconds;
		var negative = total < 0;
		total = System.Math.Abs(total);
		var days = total / 86_400_000;
		var hours = total % 86_400_000 / 3_600_000;
		var minutes = total % 3_600_000 / 60_000;
		var seconds = total % 60_000 / 1000;
		var millis = total % 1000;
		var parts = new List<string>(4);
		if (days > 0) parts.Add($"{days}d");
		if (hours > 0) parts.Add($"{hours}h");
		if (minutes > 0) parts.Add($"{minutes}m");
		if (seconds > 0 || millis > 0 || parts.Count == 0)
		{
			var fraction = millis == 0 ? string.Empty : "." + (millis / 100.0).ToString("0.###", CultureInfo.InvariantCulture);
			parts.Add($"{seconds}{fraction}s");
		}

		return (negative ? "-" : string.Empty) + string.Join(" ", parts);
	}
}

/// <summary>Дата/время: хранится в UTC, отображается в часовом поясе (spec §18c).</summary>
[KernType("datetime")]
public sealed class KernDateTime(DateTime value, TimeZoneInfo? zone = null)
{
	public DateTime Value { get; } = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

	/// <summary>Часовой пояс отображения; null — UTC.</summary>
	public TimeZoneInfo? Zone { get; } = zone;

	/// <summary>Локальное время выбранного пояса (или UTC).</summary>
	public DateTime Local => Zone is null ? Value : TimeZoneInfo.ConvertTimeFromUtc(Value, Zone);

	public override string ToString() =>
		Local.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) +
		(Zone is null ? "Z" : " " + Zone.Id);
}

/// <summary>Часовой пояс <c>time_zone</c>.</summary>
[KernType("time_zone")]
public sealed class KernTimeZone(TimeZoneInfo zone)
{
	public TimeZoneInfo Zone { get; } = zone;
}

/// <summary>Члены <c>time_zone</c>.</summary>
[KernType("time_zone")]
public static class TimeZoneApi
{
	[KernProperty]
	public static string Name(KNativeBox self) => Require(self).Id;

	/// <summary><c>tz.offset_minutes</c>: смещение от UTC сейчас.</summary>
	[KernName("offset_minutes")]
	[KernProperty]
	public static int OffsetMinutes(KNativeBox self) => (int)Require(self).GetUtcOffset(System.DateTime.UtcNow).TotalMinutes;

	private static TimeZoneInfo Require(KNativeBox self) => self.Value switch
	{
		KernTimeZone zone => zone.Zone,
		_ => throw new KernPanic("time_zone: ожидался часовой пояс"),
	};
}

/// <summary>Секундомер (<c>stopwatch</c>, spec §18c).</summary>
[KernType("stopwatch")]
public sealed class KernStopwatch
{
	private readonly Stopwatch _watch = new();

	public KernStopwatch() => _watch.Start();

	public TimeSpan Elapsed => _watch.Elapsed;

	public void Start() => _watch.Start();

	public void Stop() => _watch.Stop();

	public void Reset()
	{
		_watch.Reset();
		_watch.Start();
	}
}

/// <summary>Фабрики и члены <c>duration</c>.</summary>
[KernType("duration")]
public static class DurationApi
{
	[KernStatic]
	[KernName("from_ms")]
	public static KernDuration FromMs(long milliseconds) => new(milliseconds);

	[KernStatic]
	[KernName("from_seconds")]
	public static KernDuration FromSeconds(long seconds) => new(seconds * 1000);

	[KernStatic]
	[KernName("from_minutes")]
	public static KernDuration FromMinutes(long minutes) => new(minutes * 60_000);

	[KernStatic]
	[KernName("from_hours")]
	public static KernDuration FromHours(long hours) => new(hours * 3_600_000);

	[KernStatic]
	public static KernDuration Parse(string text)
	{
		if (!TryParse(text, out var milliseconds))
			throw new KernPanic($"duration.parse: неверная длительность '{text}'");
		return new KernDuration(milliseconds);
	}

	[KernProperty]
	public static long TotalMs(KNativeBox self) => Box(self).Milliseconds;

	[KernProperty]
	public static long TotalSeconds(KNativeBox self) => Box(self).Milliseconds / 1000;

	[KernProperty]
	public static long TotalMinutes(KNativeBox self) => Box(self).Milliseconds / 60_000;

	[KernProperty]
	public static long TotalHours(KNativeBox self) => Box(self).Milliseconds / 3_600_000;

	[KernProperty]
	public static long TotalDays(KNativeBox self) => Box(self).Milliseconds / 86_400_000;

	[KernName("to_str")]
	[KernMember]
	public static string ToStr(KNativeBox self) => Box(self).ToString();

	[KernMember(Operator = "+")]
	public static KernDuration Add(KNativeBox self, KernDuration other) =>
		new(Box(self).Milliseconds + other.Milliseconds);

	[KernMember(Operator = "-")]
	public static KernDuration Subtract(KNativeBox self, KernDuration other) =>
		new(Box(self).Milliseconds - other.Milliseconds);

	[KernMember(Operator = "*")]
	public static KernDuration Multiply(KNativeBox self, long factor) => new(Box(self).Milliseconds * factor);

	[KernMember(Operator = "<")]
	public static bool Less(KNativeBox self, KernDuration other) => Box(self).Milliseconds < other.Milliseconds;

	[KernMember(Operator = "<=")]
	public static bool LessOrEqual(KNativeBox self, KernDuration other) =>
		Box(self).Milliseconds <= other.Milliseconds;

	[KernMember(Operator = ">")]
	public static bool Greater(KNativeBox self, KernDuration other) => Box(self).Milliseconds > other.Milliseconds;

	[KernMember(Operator = ">=")]
	public static bool GreaterOrEqual(KNativeBox self, KernDuration other) =>
		Box(self).Milliseconds >= other.Milliseconds;

	[KernMember(Operator = "==")]
	public static bool Equal(KNativeBox self, KernDuration other) =>
		Box(self).Milliseconds == other.Milliseconds;

	[KernMember(Operator = "!=")]
	public static bool NotEqual(KNativeBox self, KernDuration other) =>
		Box(self).Milliseconds != other.Milliseconds;

	private static KernDuration Box(KNativeBox self) =>
		self.Value as KernDuration ?? throw new KernPanic("duration: ожидалось значение duration");

	/// <summary>Разбор <c>1h30m</c>, <c>1m 30s</c>, <c>500ms</c>, <c>1d 12h</c>.</summary>
	private static bool TryParse(string text, out long milliseconds)
	{
		milliseconds = 0;
		var index = 0;
		var any = false;
		while (index < text.Length)
		{
			while (index < text.Length && char.IsWhiteSpace(text[index]))
				index++;
			if (index >= text.Length)
				break;
			var start = index;
			while (index < text.Length && (char.IsAsciiDigit(text[index]) || text[index] == '.'))
				index++;
			if (index == start)
				return false;
			var numberText = text[start..index];
			var unitStart = index;
			while (index < text.Length && char.IsLetter(text[index]))
				index++;
			var unit = text[unitStart..index];
			if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
				unit.Length == 0)
				return false;
			var factor = unit switch
			{
				"d" => 86_400_000d,
				"h" => 3_600_000d,
				"m" => 60_000d,
				"s" => 1000d,
				"ms" => 1d,
				_ => -1d
			};
			if (factor < 0)
				return false;
			milliseconds += (long)System.Math.Round(number * factor);
			any = true;
		}

		return any;
	}
}

/// <summary>Фабрики и члены <c>datetime</c>.</summary>
[KernType("datetime")]
public static class DateTimeApi
{
	[KernStatic]
	public static KernDateTime Of(long year, long month, long day, long hour = 0, long minute = 0, long second = 0) =>
		new(new DateTime((int)year, (int)month, (int)day, (int)hour, (int)minute, (int)second, DateTimeKind.Utc));

	[KernStatic]
	public static KernDateTime Now() => new(System.DateTime.UtcNow);

	/// <summary><c>datetime.now_in(tz)</c>: текущий момент в часовом поясе.</summary>
	[KernName("now_in")]
	[KernStatic]
	public static KernDateTime NowIn(KernTimeZone zone) => new(System.DateTime.UtcNow, zone.Zone);

	/// <summary><c>dt.in_zone(tz)</c>: тот же момент в другом часовом поясе.</summary>
	[KernName("in_zone")]
	[KernMember]
	public static KernDateTime InZone(KNativeBox self, KernTimeZone zone) => new(Box(self).Value, zone.Zone);

	/// <summary><c>dt.zone_name</c>: имя пояса; пустая строка для UTC.</summary>
	[KernName("zone_name")]
	[KernProperty]
	public static string ZoneName(KNativeBox self) => Box(self).Zone?.Id ?? "";

	[KernProperty]
	public static long Year(KNativeBox self) => Box(self).Local.Year;

	[KernProperty]
	public static long Month(KNativeBox self) => Box(self).Local.Month;

	[KernProperty]
	public static long Day(KNativeBox self) => Box(self).Local.Day;

	[KernProperty]
	public static long Hour(KNativeBox self) => Box(self).Local.Hour;

	[KernProperty]
	public static long Minute(KNativeBox self) => Box(self).Local.Minute;

	[KernProperty]
	public static long Second(KNativeBox self) => Box(self).Local.Second;

	[KernProperty]
	public static long Weekday(KNativeBox self) => ((int)Box(self).Local.DayOfWeek + 6) % 7 + 1;

	[KernMember]
	public static string Format(KNativeBox self, string pattern) =>
		Box(self).Local.ToString(pattern, CultureInfo.InvariantCulture);

	[KernName("add_months")]
	[KernMember]
	public static KernDateTime AddMonths(KNativeBox self, long months) => new(Box(self).Value.AddMonths((int)months), Box(self).Zone);

	[KernMember(Operator = "+")]
	public static KernDateTime Add(KNativeBox self, KernDuration other) =>
		new(Box(self).Value.AddMilliseconds(other.Milliseconds), Box(self).Zone);

	[KernMember(Operator = "-")]
	public static KernDuration Subtract(KNativeBox self, KernDateTime other) =>
		new((long)(Box(self).Value - other.Value).TotalMilliseconds);

	[KernMember(Operator = "<")]
	public static bool Less(KNativeBox self, KernDateTime other) => Box(self).Value < other.Value;

	[KernMember(Operator = "<=")]
	public static bool LessOrEqual(KNativeBox self, KernDateTime other) => Box(self).Value <= other.Value;

	[KernMember(Operator = ">")]
	public static bool Greater(KNativeBox self, KernDateTime other) => Box(self).Value > other.Value;

	[KernMember(Operator = ">=")]
	public static bool GreaterOrEqual(KNativeBox self, KernDateTime other) =>
		Box(self).Value >= other.Value;

	[KernMember(Operator = "==")]
	public static bool Equal(KNativeBox self, KernDateTime other) => Box(self).Value == other.Value;

	[KernMember(Operator = "!=")]
	public static bool NotEqual(KNativeBox self, KernDateTime other) => Box(self).Value != other.Value;

	private static KernDateTime Box(KNativeBox self) =>
		self.Value as KernDateTime ?? throw new KernPanic("datetime: ожидалось значение datetime");
}

/// <summary>Члены <c>stopwatch</c>.</summary>
[KernType("stopwatch")]
public static class StopwatchApi
{
	[KernCtor]
	[KernName("stopwatch")]
	public static KValue Create() => KValue.Obj(new KNativeBox(new KernStopwatch()));

	[KernProperty]
	public static KernDuration Elapsed(KNativeBox self) =>
		new((long)Box(self).Elapsed.TotalMilliseconds);

	[KernMember]
	public static void Start(KNativeBox self) => Box(self).Start();

	[KernMember]
	public static void Stop(KNativeBox self) => Box(self).Stop();

	[KernMember]
	public static void Reset(KNativeBox self) => Box(self).Reset();

	private static KernStopwatch Box(KNativeBox self) =>
		self.Value as KernStopwatch ?? throw new KernPanic("stopwatch: ожидался секундомер");
}

/// <summary>Модуль <c>kern.time</c> (spec §18c).</summary>
[KernModule("kern.time")]
public static class TimeModule
{
	[KernMember]
	public static void Sleep(int milliseconds) => Thread.Sleep(milliseconds);

	[KernMember]
	public static KernDuration Measure(Interpreter interpreter, KValue function)
	{
		var watch = Stopwatch.StartNew();
		interpreter.RunValue(function);
		watch.Stop();
		return new KernDuration((long)watch.Elapsed.TotalMilliseconds);
	}

	[KernName("unix_ms")]
	[KernMember]
	public static long UnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

	[KernName("unix_s")]
	[KernMember]
	public static long UnixSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

	/// <summary><c>time.zone(name)</c>: часовой пояс по IANA/Windows-имени или <c>nil</c>.</summary>
	[KernMember]
	[KernReturn("time_zone?")]
	public static KernTimeZone? Zone(string name)
	{
		try
		{
			return TimeZoneInfo.FindSystemTimeZoneById(name) is { } zone ? new KernTimeZone(zone) : null;
		}
		catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
		{
			return null;
		}
	}

	/// <summary><c>time.zones()</c>: известные имена часовых поясов.</summary>
	[KernMember]
	[KernReturn("list<str>")]
	public static KValue Zones() =>
		KValue.Obj(new KList(null,
			[.. TimeZoneInfo.GetSystemTimeZones().Select(zone => KValue.Str(zone.Id))]));
}
