using Kern.Binding.Attributes;

namespace Kern.Runtime.IO;

/// <summary>Модуль <c>kern.io</c>: консольный ввод (spec §19, ROADMAP M6).</summary>
[KernModule("kern.io")]
public static partial class ConsoleModule
{
	[KernName("readln")]
	[KernMember]
	public static string ReadLine() => Console.In.ReadLine() ?? "";

	[KernName("read")]
	[KernMember]
	public static string ReadToEnd() => Console.In.ReadToEnd();

	[KernName("eof")]
	[KernMember]
	public static bool EndOfFile() => Console.In.Peek() < 0;

	[KernName("flush")]
	[KernMember]
	public static void Flush()
	{
		Console.Out.Flush();
		Console.Error.Flush();
	}
}
