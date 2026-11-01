using Kern.Lib.Transpile.Core;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>Транспилятор Kern → C#/Kotlin (kern.transpile).</summary>
public sealed class TranspileTests
{
	private const string Source = """
		unit Point(f64 x, f64 y)
		fun area(Point a, Point b) -> f64 {
			var w = b.x - a.x
			var h = b.y - a.y
			w * h
		}
		fun divide(i32 a, i32 b) -> result<i32, str> {
			if b == 0 { Err("деление на ноль") } else { Ok(a / b) }
		}
		fun main {
			println(area(Point(1.0, 2.0), Point(4.0, 6.0)))
		}
		""";

	[Fact]
	public void CSharp_target_emits_declarations_and_result()
	{
		TranspileResult result = new Transpiler("csharp").RunText("shapes", Source);
		string text = result.Files["Shapes.cs"];

		Assert.Contains("record struct Point", text);
		Assert.Contains("public static double Area(Point a, Point b)", text);
		Assert.Contains("KResult<int, string>", text);
		Assert.Contains("Console.WriteLine(Area(", text);
		Assert.Contains("KernRuntime", result.Files["KernRuntime.cs"]);
	}

	[Fact]
	public void Kotlin_target_emits_declarations_and_result()
	{
		TranspileResult result = new Transpiler("kotlin").RunText("shapes", Source);
		string text = result.Files["Shapes.kt"];

		Assert.Contains("data class Point(var x: Double, var y: Double)", text);
		Assert.Contains("fun area(", text);
		Assert.Contains("KResult<Int, String>", text);
		Assert.Contains("println(area(", text);
		Assert.Contains("class KResult", result.Files["KernRuntime.kt"]);
	}

	[Fact]
	public void Script_mappings_override_calls()
	{
		string path = Path.Combine(Path.GetTempPath(), "kern-map-" + Guid.NewGuid().ToString("N") + ".kern");
		File.WriteAllText(path, """
			fun map_type(str target, str kern_type, list<str> args) -> str? { nil }
			fun map_call(str target, str owner, str name, str receiver, list<str> args) -> str? {
				if owner == "" && name == "println" {
					str joined = args.join(", ")
					ret f"Log.Info(${joined})"
				}
				nil
			}
			fun map_member(str target, str owner, str name, str receiver) -> str? { nil }
			""");
		try
		{
			var transpiler = new Transpiler("csharp");
			transpiler.AddScript(path);
			string text = transpiler.RunText("shapes", Source).Files["Shapes.cs"];
			Assert.Contains("Log.Info(Area(", text);
		}
		finally
		{
			File.Delete(path);
		}
	}
}
