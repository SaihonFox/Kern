using Kern.Compiler.Semantics.Attributes;
using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Условная компиляция (spec §7): объявления с ложным <c>#[if(...)]</c> не попадают
/// в модуль, поэтому платформенные ветки живут в одном файле без <c>if</c> в рантайме.
/// </summary>
[Collection("timing")]
public sealed class ConditionalCompilationTests
{
	[Fact]
	public void Declarations_for_the_current_os_are_kept_and_others_are_removed()
	{
		string expected = OperatingSystem.IsWindows()
			? "windows"
			: OperatingSystem.IsLinux() ? "linux" : "macos";
		const string source = """
			#[if(os = "windows")] fun platform() -> str "windows"
			#[if(os = "linux")] fun platform() -> str "linux"
			#[if(os = "macos")] fun platform() -> str "macos"
			""";

		Assert.Equal(KValue.Str(expected), StdPipeline.Run(source, "platform"));
	}

	[Fact]
	public void Target_wasm_is_excluded_from_cli_builds()
	{
		const string source = """
			#[if(target = "wasm")] fun platform() -> str "wasm"
			#[if(target = "cli")] fun platform() -> str "cli"
			""";

		Assert.Equal(KValue.Str("cli"), StdPipeline.Run(source, "platform"));
	}

	[Fact]
	public void Conditions_apply_to_members_inside_a_type()
	{
		string expected = OperatingSystem.IsWindows()
			? "windows"
			: OperatingSystem.IsLinux() ? "linux" : "macos";
		const string source = """
			type Host {
				#[if(os = "windows")] fun name() -> str "windows"
				#[if(os = "linux")] fun name() -> str "linux"
				#[if(os = "macos")] fun name() -> str "macos"
			}
			fun name() -> str Host().name()
			""";

		Assert.Equal(KValue.Str(expected), StdPipeline.Run(source, "name"));
	}

	[Fact]
	public void Invalid_condition_is_a_compilation_error()
	{
		var module = StdPipeline.CompileOrNull(
			"""#[if(os = "solaris")] fun f() -> i32 1""",
			out var diagnostics);

		Assert.Null(module);
		Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KRN2101" && diagnostic.IsError);
	}

	[Fact]
	public void Architecture_condition_is_evaluated()
	{
		string expected = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
		{
			System.Runtime.InteropServices.Architecture.X64 => "x64",
			System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
			System.Runtime.InteropServices.Architecture.X86 => "x86",
			System.Runtime.InteropServices.Architecture.Arm => "arm",
			_ => "other",
		};
		const string source = """
			#[if(arch = "x64")] fun arch() -> str "x64"
			#[if(arch = "arm64")] fun arch() -> str "arm64"
			""";

		Assert.Equal(KValue.Str(expected), StdPipeline.Run(source, "arch"));
	}

	[Fact]
	public void Known_attribute_catalog_includes_if()
	{
		Assert.True(AttributeCatalog.IsKnown("if"));
	}
}
