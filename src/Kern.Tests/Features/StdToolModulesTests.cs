using Kern.Runtime.Logging;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Нативные модули инструментов (spec §16, раунд 27): <c>kern.log</c>, <c>kern.cli</c>,
/// <c>kern.config</c>, <c>kern.process</c>. Тесты идут через общий конвейер std-модулей.
/// </summary>
[Collection("timing")]
public sealed class StdToolModulesTests
{
	[Fact]
	public void Log_filters_levels_and_formats_lines()
	{
		var writer = new StringWriter { NewLine = "\n" };
		TextWriter original = Console.Out;
		Console.SetOut(writer);
		try
		{
			const string source = """
				fun emit() {
					log.to_stdout()
					log.set_level("info")
					log.trace("trace-hidden")
					log.debug("debug-hidden")
					log.info("visible")
					log.warn("careful")
				}
				""";

			Assert.Equal(KValue.Void, StdPipeline.Run(source, "emit"));
			string text = writer.ToString();
			Assert.DoesNotContain("trace-hidden", text);
			Assert.DoesNotContain("debug-hidden", text);
			Assert.Contains("visible", text);
			Assert.Contains("careful", text);
			Assert.Matches(
				@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z INFO  visible",
				text);
			Assert.Matches(
				@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z WARN  careful",
				text);
			Assert.Equal(2, text.TrimEnd('\n').Split('\n').Length);
		}
		finally
		{
			Console.SetOut(original);
			LogApi.SetLevel("info");
			LogApi.ToStderr();
		}
	}

	[Fact]
	public void Log_unknown_level_panics_in_russian()
	{
		const string source = """
			fun bad() { log.set_level("nope") }
			""";

		KernPanic panic = StdPipeline.RunPanics(source, "bad");
		Assert.Contains("уровень", panic.Message);
	}

	[Fact]
	public void Log_writes_to_file_and_exposes_settable_level()
	{
		string directory = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			$"kern_log_{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		string path = System.IO.Path.Combine(directory, "app.log").Replace('\\', '/');
		try
		{
			var source = $$"""
				fun setup() -> bool {
					log.set_level("info")
					match log.to_file("{{path}}") {
						Ok(_) => true
						Err(_) => false
					}
				}
				fun emit() { log.info("file-line") }
				fun close_it() { log.close() }
				fun set_and_get() -> str {
					log.set_level("warn")
					log.level
				}
				""";

			Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "setup"));
			Assert.Equal(KValue.Void, StdPipeline.Run(source, "emit"));
			Assert.Equal(KValue.Void, StdPipeline.Run(source, "close_it"));
			string text = System.IO.File.ReadAllText(path);
			Assert.Matches(
				@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z INFO  file-line",
				text);
			Assert.Equal(KValue.Str("warn"), StdPipeline.Run(source, "set_and_get"));
		}
		finally
		{
			LogApi.SetLevel("info");
			LogApi.ToStderr();
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Cli_parses_flags_options_and_positionals()
	{
		const string source = """
			fun parsed() -> str {
				var p = cli.parser("app", "демо")
				p.flag("verbose", "v", "подробно")
				p.option("name", "n", "anon", "имя")
				match p.parse(["-v", "--name=bob", "file.txt"]) {
					Ok(values) => f"${values.flag("verbose")}/${values.option("name")}/${values.positional().len}"
					Err(error) => error
				}
			}
			fun attached() -> str {
				var p = cli.parser("app", "")
				p.option("name", "n", "", "")
				match p.parse(["-nbob"]) {
					Ok(values) => values.option("name")
					Err(error) => error
				}
			}
			fun default_value() -> str {
				var p = cli.parser("app", "")
				p.option("mode", "m", "fast", "")
				match p.parse(<str>[]) {
					Ok(values) => values.option("mode")
					Err(error) => error
				}
			}
			fun end_of_options() -> i32 {
				var p = cli.parser("app", "")
				p.flag("verbose", "v", "")
				match p.parse(["-v", "--", "-x", "y"]) {
					Ok(values) => values.positional().len
					Err(_) => -1
				}
			}
			fun unknown_is_err() -> bool {
				var p = cli.parser("app", "")
				match p.parse(["--bogus"]) {
					Ok(_) => false
					Err(error) => error.len > 0
				}
			}
			fun help_seen() -> bool {
				var p = cli.parser("app", "")
				match p.parse(["-h"]) {
					Ok(values) => values.help_requested
					Err(_) => false
				}
			}
			fun usage_prefix() -> bool {
				var p = cli.parser("app", "демо")
				p.flag("verbose", "v", "подробно")
				p.usage().starts_with("Usage: app [options] [positional...]")
			}
			""";

		Assert.Equal(KValue.Str("true/bob/1"), StdPipeline.Run(source, "parsed"));
		Assert.Equal(KValue.Str("bob"), StdPipeline.Run(source, "attached"));
		Assert.Equal(KValue.Str("fast"), StdPipeline.Run(source, "default_value"));
		Assert.Equal(KValue.I32(2), StdPipeline.Run(source, "end_of_options"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "unknown_is_err"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "help_seen"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "usage_prefix"));
	}

	[Fact]
	public void Config_flattens_json_and_reads_typed_values()
	{
		const string json = """{"db":{"host":"localhost","port":5432},"debug":true,"tags":["a","b"],"name":"demo"}""";
		string escaped = json.Replace("\"", "\\\"");
		var source = $$"""
			fun host() -> str {
				match config.from_str("{{escaped}}", "json") {
					Ok(c) => c.get_or("db.host", "")
					Err(error) => error
				}
			}
			fun port() -> i64 {
				match config.from_str("{{escaped}}", "json") {
					Ok(c) => match c.get_i64("db.port") {
						nil => -1
						value => value
					}
					Err(_) => -2
				}
			}
			fun debug() -> bool {
				match config.from_str("{{escaped}}", "json") {
					Ok(c) => match c.get_bool("debug") {
						nil => false
						value => value
					}
					Err(_) => false
				}
			}
			fun tags() -> i32 {
				match config.from_str("{{escaped}}", "json") {
					Ok(c) => c.get_list("tags").len
					Err(_) => -1
				}
			}
			fun comma_list() -> i32 {
				match config.from_str("list=a,b,c", "kv") {
					Ok(c) => c.get_list("list").len
					Err(_) => -1
				}
			}
			fun bad_number_is_nil() -> bool {
				match config.from_str("{{escaped}}", "json") {
					Ok(c) => c.get_i64("db.host") == nil
					Err(_) => false
				}
			}
			fun keys_has() -> bool {
				match config.from_str("{{escaped}}", "json") {
					Ok(c) => c.has("db.port") && c.keys().len == 5
					Err(_) => false
				}
			}
			""";

		Assert.Equal(KValue.Str("localhost"), StdPipeline.Run(source, "host"));
		Assert.Equal(KValue.I64(5432), StdPipeline.Run(source, "port"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "debug"));
		Assert.Equal(KValue.I32(2), StdPipeline.Run(source, "tags"));
		Assert.Equal(KValue.I32(3), StdPipeline.Run(source, "comma_list"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "bad_number_is_nil"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "keys_has"));
	}

	[Fact]
	public void Process_run_reports_output_exit_code_and_which()
	{
		bool windows = OperatingSystem.IsWindows();
		string shellCommand = windows ? "cmd.exe" : "/bin/sh";
		string shellFlag = windows ? "/c" : "-c";
		string whichName = windows ? "cmd.exe" : "sh";
		string echo = $"process.run(\"{shellCommand}\", [\"{shellFlag}\", \"echo hello\"])";
		string exit = $"process.run(\"{shellCommand}\", [\"{shellFlag}\", \"exit 3\"])";
		string spawn = $"process.spawn(\"{shellCommand}\", [\"{shellFlag}\", \"echo spawned\"])";
		var source = $$"""
			fun out() -> str {
				match {{echo}} {
					Ok(output) => output.stdout.trim()
					Err(error) => error
				}
			}
			fun code() -> i32 {
				match {{exit}} {
					Ok(output) => output.code
					Err(_) => -1
				}
			}
			fun via_shell() -> str {
				match process.shell("echo hello") {
					Ok(output) => output.stdout.trim()
					Err(error) => error
				}
			}
			fun cannot_start() -> bool {
				match process.run("kern_missing_binary_123", <str>[]) {
					Ok(_) => false
					Err(error) => error.len > 0
				}
			}
			fun found() -> bool { process.which("{{whichName}}") != nil }
			fun missing() -> bool { process.which("kern_missing_binary_123") == nil }
			fun spawn_line() -> str {
				var handle = {{spawn}}
				str? line = handle.stdout_read_line()
				i32 code = handle.wait()
				f"${line ?: ""}/${code}"
			}
			""";

		Assert.Equal(KValue.Str("hello"), StdPipeline.Run(source, "out"));
		Assert.Equal(KValue.I32(3), StdPipeline.Run(source, "code"));
		Assert.Equal(KValue.Str("hello"), StdPipeline.Run(source, "via_shell"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "cannot_start"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "found"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "missing"));
		Assert.Equal(KValue.Str("spawned/0"), StdPipeline.Run(source, "spawn_line"));
	}
}
