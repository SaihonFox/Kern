using Kern.Binding.Attributes;
using Kern.Binding.Formatting;
using Kern.Binding.Marshalling;
using Kern.Vm.Values;

namespace Kern.Runtime.Core;

/// <summary>
/// Модуль <c>kern.core</c>: базовый ввод-вывод, panic, пауза и выход
/// (spec §19, ROADMAP M6). Консоль всегда UTF-8, настройка — в CLI.
/// </summary>
[KernModule("kern.core")]
public static class CoreModule
{
	[KernName("print")]
	[KernMember]
	public static void Print(KValue value) => Console.Out.Write(KernFormat.Value(value));

	[KernMember]
	public static void Println(KValue value) => Console.Out.WriteLine(KernFormat.Value(value));

	[KernMember]
	public static void Println() => Console.Out.WriteLine();

	[KernMember]
	public static void Eprintln(KValue value) => Console.Error.WriteLine(KernFormat.Value(value));

	[KernName("panic")]
	[KernMember]
	public static void Panic(string message) => throw new Kern.Vm.Execution.KernPanic(message);

	[KernName("delay")]
	[KernMember]
	public static Task Delay(int milliseconds) => Task.Delay(milliseconds);

	[KernName("exit")]
	[KernMember]
	public static void Exit(int code) => Environment.Exit(code);

	[KernName("format")]
	[KernMember]
	public static string Format(KValue value) => KernFormat.Value(value);

	[KernName("to_str")]
	[KernMember]
	public static string ToStr(KValue value) => KernFormat.Value(value);

	/// <summary><c>format(value, spec)</c> для интерполяции <c>f"${value:spec}"</c> (spec §3).</summary>
	[KernName("format")]
	[KernMember]
	public static string FormatWithSpec(KValue value, string spec) => KernFormat.Format(value, spec);

	/// <summary>Внутренний помощник для нативов, которым нужно вернуть значение из CLR-типа.</summary>
	internal static KValue Box(object? value) => KMarshal.FromObject(value);
}
