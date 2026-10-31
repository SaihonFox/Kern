using System.Diagnostics;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;

namespace Kern.Runtime.Time;

/// <summary>Время: <c>time.now_ms()</c>, форматирование unix-времени, замеры, блокирующий сон. Для сложных случаев — <c>use dotnet.System[DateTime]</c>.</summary>
[KernType("time")]
public static class TimeApi
{
	private static readonly Stopwatch Clock = Stopwatch.StartNew();

	[KernStatic] public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	[KernStatic] public static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
	/// <summary>Монотонные миллисекунды с запуска процесса (для замеров).</summary>
	[KernStatic] public static double Elapsed() => Clock.Elapsed.TotalMilliseconds;

	/// <summary>Текущее время UTC в формате ISO 8601.</summary>
	[KernStatic] public static string NowIso() => DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>Форматирует unix-секунды (UTC) по .NET-шаблону, например <c>"yyyy-MM-dd HH:mm:ss"</c>.</summary>
	[KernStatic] public static string Format(long unixSeconds, string pattern = "yyyy-MM-dd HH:mm:ss") =>
		DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString(pattern, System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>Разбор даты ISO 8601 в unix-секунды; nil при ошибке.</summary>
	[KernStatic] public static long? ParseIso(string text) =>
		DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
			? d.ToUnixTimeSeconds() : null;

	/// <summary>Блокирующий сон. В async-коде используйте <c>await delay(ms)</c>.</summary>
	[KernStatic] public static void Sleep(int milliseconds) => Thread.Sleep(System.Math.Max(0, milliseconds));

	/// <summary>Замер: выполняет функцию и возвращает, сколько она работала (<c>duration</c>).</summary>
	[KernStatic] public static KDuration Measure(KAction body)
	{
		var sw = Stopwatch.StartNew();
		body.Invoke();
		return new KDuration(sw.Elapsed);
	}

	/// <summary>Монотонное время с запуска процесса как <c>duration</c>.</summary>
	[KernStatic] public static KDuration Uptime() => new(Clock.Elapsed);
}
