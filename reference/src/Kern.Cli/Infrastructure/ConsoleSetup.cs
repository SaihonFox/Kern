using System.Text;

namespace Kern.Cli.Infrastructure;

/// <summary>Консоль в UTF-8 без BOM (иначе русский текст превращается в «?»).</summary>
internal static class ConsoleSetup
{
	public static void UseUtf8()
	{
		var utf8 = new UTF8Encoding(false);
		Console.OutputEncoding = utf8;
		Console.InputEncoding = utf8;
		Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8, 4096) { AutoFlush = true });
		Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8, 1024) { AutoFlush = true });
		Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8));
	}
}
