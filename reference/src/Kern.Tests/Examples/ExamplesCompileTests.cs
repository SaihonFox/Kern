using Kern.Tests.Harness;
using Xunit;

namespace Kern.Tests.Examples;

/// <summary>Каждый пример из examples/ (папка с kern.toml) должен проходить разбор и типизацию без ошибок.</summary>
public class ExamplesCompileTests
{
	public static TheoryData<string> Projects()
	{
		var data = new TheoryData<string>();
		foreach (var d in Directory.EnumerateDirectories(Path.Combine(KernHarness.RepoRoot(), "examples")).OrderBy(d => d))
			if (File.Exists(Path.Combine(d, "kern.toml"))) data.Add(Path.GetFileName(d));
		return data;
	}

	[Theory]
	[MemberData(nameof(Projects))]
	public void ExampleCompiles(string name)
	{
		string dir = Path.Combine(KernHarness.RepoRoot(), "examples", name);
		var project = KernHarness.CreateLoader().LoadDirectory(dir);
		var errors = KernHarness.Errors(project);
		Assert.True(errors.Count == 0, string.Join("\n", errors));
	}
}
