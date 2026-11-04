using Kern.Binding.Catalog;
using Kern.Cli;
using Kern.Vm.Native;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// <c>use dotnet</c>: открытые generic-типы BCL, <c>params</c>-развёртывание,
/// <c>ref</c>/<c>out</c>-параметры и делегаты (spec §12.10).
/// </summary>
[Collection("timing")]
public sealed class DotnetInteropAdvancedTests
{
	[Fact]
	public void Open_generic_type_imports_members_and_delegates()
	{
		Type? list = DotnetCatalog.FindType("System.Collections.Generic.List");
		Assert.NotNull(list);
		NativeCatalog catalog = DotnetCatalog.Import(Support.StdPipeline.Catalog(), [list!]);

		Assert.Contains(catalog.Types, type => type.Name == "List" && type.GenericArity == 1);
		Assert.Contains(catalog.Functions, function => function.Name == "Add" && function.SelfType == "List<T>");
		Assert.Contains(catalog.Functions, function => function.Name == "Count" && function.Kind == NativeMemberKind.PropertyGet);
		Assert.Contains(catalog.Functions, function => function.Name == "ForEach");
	}

	[Fact]
	public void Open_generic_List_is_usable()
	{
		var output = Run("""
			use dotnet.System.Collections.Generic[List]

			fun main {
				List<i32> xs = List<i32>()
				xs.Add(1)
				xs.Add(2)
				println(xs.Count)
				println(xs.Contains(2))
			}
			""");

		Assert.Equal("2\ntrue\n", output);
	}

	[Fact]
	public void Open_generic_Dictionary_is_usable()
	{
		var output = Run("""
			use dotnet.System.Collections.Generic[Dictionary]

			fun main {
				Dictionary<str, i32> d = Dictionary<str, i32>()
				d.Add("a", 1)
				println(d.Count)
				println(d.ContainsKey("a"))
				println(d.ContainsKey("b"))
			}
			""");

		Assert.Equal("1\ntrue\nfalse\n", output);
	}

	[Fact]
	public void Params_accepts_variadic_arguments()
	{
		var output = Run("""
			use dotnet.System[String]

			fun main {
				println(String.Join("-", "a", "b", "c"))
			}
			""");

		Assert.Equal("a-b-c\n", output);
	}

	[Fact]
	public void Explicit_array_still_binds_to_params()
	{
		var output = Run("""
			use dotnet.System[String]

			fun main {
				array<str> parts = <str>["a", "b"]
				println(String.Join("+", parts))
			}
			""");

		Assert.Equal("a+b\n", output);
	}

	[Fact]
	public void ByRef_out_parameter_writes_back()
	{
		var output = Run("""
			use dotnet.System[Int32]

			fun main {
				i32 value = 0
				bool ok = Int32.TryParse("42", *value)
				println(ok)
				println(value)
			}
			""");

		Assert.Equal("true\n42\n", output);
	}

	[Fact]
	public void Kern_function_becomes_Action_delegate()
	{
		var output = Run("""
			use dotnet.System.Collections.Generic[List]

			fun main {
				List<i32> xs = List<i32>()
				xs.Add(1)
				xs.Add(2)
				xs.ForEach(fun(i32 x) { println(x) })
			}
			""");

		Assert.Equal("1\n2\n", output);
	}

	[Fact]
	public void Kern_function_becomes_Predicate_delegate()
	{
		var output = Run("""
			use dotnet.System.Collections.Generic[List]

			fun main {
				List<i32> xs = List<i32>()
				xs.Add(1)
				xs.Add(2)
				println(xs.Find(fun(i32 x) -> bool x > 1))
				println(xs.Exists(fun(i32 x) -> bool x == 1))
			}
			""");

		Assert.Equal("2\ntrue\n", output);
	}

	private static string Run(string source)
	{
		string directory = Path.Combine(Path.GetTempPath(), $"kern-dotnet-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		string file = Path.Combine(directory, "main.kern");
		System.IO.File.WriteAllText(file, source, new System.Text.UTF8Encoding(false));
		try
		{
			var output = new StringWriter();
			var error = new StringWriter();
			TextWriter previous = Console.Out;
			Console.SetOut(output);
			int code;
			try
			{
				code = KernCli.Run(["run", file], output, error);
			}
			finally
			{
				Console.SetOut(previous);
			}

			Assert.True(code == 0, error.ToString());
			Assert.Equal(string.Empty, error.ToString());
			return output.ToString().Replace("\r\n", "\n");
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}
}
