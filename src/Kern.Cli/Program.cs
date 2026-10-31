using Kern.Cli.Infrastructure;

namespace Kern.Cli;

internal static class Program
{
	private static int Main(string[] args)
	{
		ConsoleSetup.UseUtf8();
		return KernCli.Run(args);
	}
}
