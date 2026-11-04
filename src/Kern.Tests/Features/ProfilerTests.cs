using Kern.Binding.Resolution;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Профайлер (spec §7a): плоская таблица, дерево вызовов и горячие строки. Дерево строится
/// по токенам кадров, поэтому вложенные вызовы видны как дети, а не как соседние корни.
/// </summary>
public sealed class ProfilerTests
{
	[Fact]
	public void Report_contains_flat_table_and_call_tree()
	{
		const string source = """
			fun inner(i32 x) -> i32 x + 1

			fun work(i32 x) -> i32 inner(x) + inner(x + 1)

			fun main() -> i32 work(1)
			""";

		var interpreter = new Interpreter(
			StdPipeline.Compile(source).Module,
			new NativeResolver(StdPipeline.Catalog()))
		{
			Profiler = new Profiler(),
		};
		Assert.Equal(KValue.I32(5), interpreter.Run("main"));

		var output = new StringWriter();
		interpreter.Profiler!.WriteReport(output);
		string report = output.ToString();

		Assert.Contains("; профиль: функции", report);
		Assert.Contains("; дерево вызовов", report);
		Assert.Contains("work", report);
		Assert.Contains("  inner", report);
		Assert.Contains("; горячие строки", report);
	}

	[Fact]
	public void Nested_calls_are_children_of_their_caller()
	{
		const string source = """
			fun leaf() -> i32 1

			fun branch() -> i32 leaf() + leaf()

			fun main() -> i32 branch()
			""";

		var interpreter = new Interpreter(
			StdPipeline.Compile(source).Module,
			new NativeResolver(StdPipeline.Catalog()))
		{
			Profiler = new Profiler(),
		};
		_ = interpreter.Run("main");

		var output = new StringWriter();
		interpreter.Profiler!.WriteReport(output);
		string[] lines = [.. output.ToString().Split('\n')];

		int tree = Array.FindIndex(lines, line => line.StartsWith("; дерево вызовов", StringComparison.Ordinal));
		Assert.True(tree >= 0, output.ToString());
		int branch = Array.FindIndex(lines, tree, line => line.StartsWith("branch", StringComparison.Ordinal));
		Assert.True(branch >= 0, output.ToString());
		Assert.True(lines[branch + 1].StartsWith("  leaf", StringComparison.Ordinal), output.ToString());
	}
}
