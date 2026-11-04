using Kern.Binding.Catalog;
using Kern.Cli;
using Kern.Tests.Features.Support;
using Kern.Vm.Native;
using Xunit;

namespace Kern.Tests.Features;

/// <summary><c>use dotnet.X[...]</c>: статические методы BCL доступны как члены типа.</summary>
[Collection("timing")]
public sealed class DotnetInteropTests
{
	[Fact]
	public void System_console_is_reflected()
	{
		Type? console = DotnetCatalog.FindType("System.Console");
		Assert.NotNull(console);
		NativeCatalog catalog = DotnetCatalog.Import(StdPipeline.Catalog(), [console!]);
		Assert.Contains(catalog.Types, type => type.Name == "Console");
		Assert.Contains(catalog.Functions, function =>
			function.Owner == "Console" && function.Name == "WriteLine");
	}

	[Fact]
	public void Kernel_program_can_call_dotnet_static_methods()
	{
		string directory = Path.Combine(Path.GetTempPath(), $"kern-dotnet-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		string file = Path.Combine(directory, "main.kern");
		System.IO.File.WriteAllText(file, """
			use dotnet.System.IO[File, Path]
			use dotnet.System[Math]

			fun main {
				str path = "kern-dotnet-interop.tmp"
				File.WriteAllText(path, "ok")
				println(File.ReadAllText(path))
				println(Path.GetFileNameWithoutExtension("a/b.txt"))
				println(Math.Abs(-3.0))
			}
			""", new System.Text.UTF8Encoding(false));
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

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error.ToString());
			Assert.Equal("ok\nb\n3\n", output.ToString().Replace("\r\n", "\n"));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Import_includes_instance_members_and_constructors()
	{
		Type? builder = DotnetCatalog.FindType("System.Text.StringBuilder");
		Assert.NotNull(builder);
		NativeCatalog catalog = DotnetCatalog.Import(StdPipeline.Catalog(), [builder!]);
		Assert.Contains(catalog.Types, type => type.Name == "StringBuilder");
		Assert.Contains(catalog.Functions, function =>
			function.Name == "Append" && function.SelfType == "StringBuilder");
		Assert.Contains(catalog.Functions, function =>
			function.Kind == NativeMemberKind.Constructor && function.Owner == "StringBuilder");
	}

	[Fact]
	public void Kernel_program_can_use_dotnet_instance_members_and_constructors()
	{
		string directory = Path.Combine(Path.GetTempPath(), $"kern-dotnet-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		string file = Path.Combine(directory, "main.kern");
		System.IO.File.WriteAllText(file, """
			use dotnet.System.Text[StringBuilder]

			fun main {
				StringBuilder b = StringBuilder()
				b.Append("a")
				b.Append("b")
				println(b.ToString())
				println(b.Length)
			}
			""", new System.Text.UTF8Encoding(false));
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

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error.ToString());
			Assert.Equal("ab\n2\n", output.ToString().Replace("\r\n", "\n"));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}
}
