using Kern.Binding.Attributes;

namespace Kern.Runtime.IO;

/// <summary>Свободные функции kern.io (prelude): ввод с консоли.</summary>
[KernModule("kern.io")]
public static class ConsoleApi
{
	/// <summary>Читает строку из stdin; <c>nil</c> на конце потока.</summary>
	[KernMember] public static string? Readln() => Console.In.ReadLine();

	[KernMember] public static string? Readln(string prompt)
	{
		Console.Out.Write(prompt);
		Console.Out.Flush();
		return Console.In.ReadLine();
	}
}
