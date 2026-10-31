namespace Kern.Tests.Harness;

public sealed class KernHarness
{
	public string Run(string source)
	{
		ArgumentNullException.ThrowIfNull(source);
		return string.Empty;
	}
}
