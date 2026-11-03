using Kern.Binding.Resolution;
using Kern.Cli.Infrastructure;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Capabilities (spec §7/§16): <c>#[needs(...)]</c> проверяется binder'ом, а
/// <c>--deny</c> — VM-политикой перед нативным вызовом. По умолчанию разрешено всё.
/// </summary>
public sealed class CapabilityTests
{
	[Fact]
	public void Declared_needs_must_cover_used_capabilities()
	{
		var module = StdPipeline.CompileOrNull(
			"""
			#[needs(net)]
			fun main() {
				println(file.read_text("data.txt"))
			}
			""",
			out var diagnostics);

		Assert.Null(module);
		Assert.Contains(diagnostics, diagnostic =>
			diagnostic.Code == "KRN2401" && diagnostic.Message.Contains("fs.read"));
	}

	[Fact]
	public void Without_needs_compiles_and_is_left_to_runtime_policy()
	{
		var module = StdPipeline.CompileOrNull(
			"""
			fun main() {
				println(file.read_text("data.txt"))
			}
			""",
			out var diagnostics);

		Assert.NotNull(module);
		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.IsError);
	}

	[Fact]
	public void Needs_with_fs_scope_compiles()
	{
		var module = StdPipeline.CompileOrNull(
			"""
			#[needs(net, fs.read("data/"), fs.write("out/"))]
			fun main() {}
			""",
			out var diagnostics);

		Assert.NotNull(module);
		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.IsError);
	}

	[Fact]
	public void Runtime_policy_allows_by_default_and_denies_native_capabilities()
	{
		string file = Path.Combine(Path.GetTempPath(), "kern-cap-" + Guid.NewGuid().ToString("N") + ".txt");
		File.WriteAllText(file, "secret");
		try
		{
			string source = $$"""
				#[needs(fs.read("."))]
				fun read_it(str path) -> str {
					match file.read_text(path) {
						Ok(value) => value
						Err(message) => panic(message)
					}
				}
				""";
			var program = StdPipeline.Compile(source);
			var allowed = new Interpreter(program.Module, new NativeResolver(StdPipeline.Catalog()))
			{
				Capabilities = CapabilityPolicy.AllowAll,
			};
			Assert.Equal(KValue.Str("secret"), allowed.Run("read_it", KValue.Str(file)));

			var denied = new Interpreter(program.Module, new NativeResolver(StdPipeline.Catalog()))
			{
				Capabilities = new CapabilityPolicy(["fs.read"]),
			};
			Assert.Throws<KernPanic>(() => denied.Run("read_it", KValue.Str(file)));

			var deniedAll = new Interpreter(program.Module, new NativeResolver(StdPipeline.Catalog()))
			{
				Capabilities = new CapabilityPolicy(["all"]),
			};
			Assert.Throws<KernPanic>(() => deniedAll.Run("read_it", KValue.Str(file)));
		}
		finally
		{
			File.Delete(file);
		}
	}

	[Fact]
	public void Cli_parses_deny_list_and_rejects_removed_allow()
	{
		string file = Path.Combine(Path.GetTempPath(), "kern-cap-cli-" + Guid.NewGuid().ToString("N") + ".kern");
		File.WriteAllText(file, "fun main() {}");
		try
		{
			var stderr = new StringWriter();
			bool parsed = SourceArguments.TryParse(
				["--deny", "fs.write,net", file],
				"run",
				allowOutput: false,
				allowProgramArguments: false,
				stderr,
				out _,
				out _,
				out _,
				out _,
				out CliOptions options);

			Assert.True(parsed, stderr.ToString());
			Assert.Equal(["fs.write", "net"], options.DenyCapabilities);

			var allowError = new StringWriter();
			bool allowParsed = SourceArguments.TryParse(
				["--allow", "net", file],
				"run",
				allowOutput: false,
				allowProgramArguments: false,
				allowError,
				out _,
				out _,
				out _,
				out _,
				out _);
			Assert.False(allowParsed);
			Assert.Contains("--allow удалён", allowError.ToString());
		}
		finally
		{
			File.Delete(file);
		}
	}
}
