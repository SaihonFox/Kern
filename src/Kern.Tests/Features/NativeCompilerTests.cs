using System.Diagnostics;
using Kern.Cli.Build;
using Kern.Cli.Infrastructure;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Выбор пути <c>kernc build --native</c>: NativeAOT для совместимых программ и
/// single-file fallback для generic-нативов, внешних DLL, reflection и другого RID.
/// </summary>
[Collection("timing")]
public sealed class NativeCompilerTests
{
	[Fact]
	public void Simple_program_uses_native_aot()
	{
		CompiledProgram program = Compile("fun main() { println(1) }");
		Assert.True(NativeCompiler.AotCompatible(program, null, new CliOptions(), out _));
	}

	[Fact]
	public void Generic_native_falls_back_to_single_file()
	{
		CompiledProgram program = Compile(
			"""
			unit Point(i32 x)

			fun main() {
				json.from_str<Point>("{\"x\":1}")
			}
			""");
		Assert.False(NativeCompiler.AotCompatible(program, null, new CliOptions(), out string? reason));
		Assert.Contains("generic", reason);
	}

	[Fact]
	public void External_libraries_and_foreign_rid_fall_back()
	{
		CompiledProgram program = Compile("fun main() { println(1) }");
		var target = new TargetModel { Name = "app", Libs = ["kern.db"] };
		Assert.False(NativeCompiler.AotCompatible(program, target, new CliOptions(), out string? libraryReason));
		Assert.Contains("DLL", libraryReason);

		string foreign = OperatingSystem.IsWindows() ? "linux-x64" : "win-x64";
		Assert.False(NativeCompiler.AotCompatible(
			program,
			null,
			new CliOptions { TargetRid = foreign },
			out string? ridReason));
		Assert.Contains("текущей платформы", ridReason);
	}

	[Fact]
	public void Native_aot_end_to_end()
	{
		// Тяжёлый тест (dotnet publish NativeAOT, ~1-2 мин); включается явно:
		//   $env:KERN_TEST_NATIVE_AOT = "1"; dotnet test --filter NativeCompilerTests
		if (Environment.GetEnvironmentVariable("KERN_TEST_NATIVE_AOT") != "1")
			return;

		string directory = Path.Combine(Path.GetTempPath(), "kern-aot-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		string source = Path.Combine(directory, "main.kern");
		File.WriteAllText(source, "fun main { println(\"aot\") }");
		try
		{
			var stderr = new StringWriter();
			string exe = NativeCompiler.BuildFile(source, Path.Combine(directory, "out"), new CliOptions(), stderr);
			Assert.True(File.Exists(exe), stderr.ToString());

			var start = new ProcessStartInfo(exe)
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			using Process process = Process.Start(start)!;
			string output = process.StandardOutput.ReadToEnd();
			process.WaitForExit();
			Assert.Equal(0, process.ExitCode);
			Assert.Contains("aot", output);
		}
		finally
		{
			try
			{
				Directory.Delete(directory, recursive: true);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	private static CompiledProgram Compile(string source)
	{
		string file = Path.Combine(Path.GetTempPath(), "kern-native-test-" + Guid.NewGuid().ToString("N") + ".kern");
		File.WriteAllText(file, source);
		try
		{
			CompileResult result = SourceCompiler.Compile(file, CompileOptions.Default);
			Assert.True(result.Success, string.Join('\n', result.ProjectMessages));
			return result.Program!;
		}
		finally
		{
			File.Delete(file);
		}
	}
}
