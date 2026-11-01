using Kern.Cli.Infrastructure;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>M8/M11: флаги CLI <c>-O0..-O2</c>, <c>--time</c>, <c>-p профиль</c> (docs/BUILDER.md §7).</summary>
public sealed class CliFlagTests
{
	[Fact]
	public void Optimization_time_and_profile_flags_are_parsed()
	{
		string file = TempSource();
		try
		{
			var stderr = new StringWriter();
			bool ok = SourceArguments.TryParse(
				[file, "-O2", "--time", "-p", "release"],
				"run", allowOutput: false, allowProgramArguments: true, stderr,
				out string source, out SourceKind kind, out _, out _, out CliOptions options);

			Assert.True(ok, stderr.ToString());
			Assert.Equal(file, source);
			Assert.Equal(SourceKind.File, kind);
			Assert.Equal(2, options.Optimize);
			Assert.True(options.Time);
			Assert.Equal("release", options.Profile);
			Assert.Equal(2, options.Apply(CompileOptions.Default).Optimize);
		}
		finally
		{
			File.Delete(file);
		}
	}

	[Fact]
	public void Without_flags_defaults_apply()
	{
		string file = TempSource();
		try
		{
			var stderr = new StringWriter();
			bool ok = SourceArguments.TryParse(
				[file],
				"check", allowOutput: false, allowProgramArguments: false, stderr,
				out _, out _, out _, out _, out CliOptions options);

			Assert.True(ok, stderr.ToString());
			Assert.Null(options.Optimize);
			Assert.False(options.Time);
			Assert.Equal(CompileOptions.Default.Optimize, options.Apply(CompileOptions.Default).Optimize);
		}
		finally
		{
			File.Delete(file);
		}
	}

	private static string TempSource()
	{
		string path = Path.Combine(Path.GetTempPath(), $"kern_flags_{Guid.NewGuid():N}.kern");
		File.WriteAllText(path, "fun main\n");
		return path;
	}
}
