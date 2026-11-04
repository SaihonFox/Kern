using Kern.Cli;
using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// M7 batch 2: <c>kern.csv</c>, <c>kern.yaml</c>, <c>kern.crypto</c>, <c>kern.uuid</c>,
/// <c>kern.xml</c> и <c>regex_match</c> (spec §16/§18).
/// </summary>
public sealed class StdModulesBatchTwoTests
{
	[Fact]
	public void Csv_parses_rows_and_dicts()
	{
		string directory = Path.Combine(Path.GetTempPath(), "kern-csv-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(directory, "src"));
		File.WriteAllText(Path.Combine(directory, "src", "app.kern"), """
			use kern.csv[csv_parse, csv_parse_dicts]
			fun main {
				match csv_parse("a,b\n1,2\n") {
					Ok(value) => println(value.len)
					Err(_) => println(-1)
				}
				match csv_parse_dicts("name,age\nаня,25\n") {
					Ok(records) => println(records[0]["name"])
					Err(_) => println("error")
				}
				match csv_parse_dicts("name\n\"петров, иван\"\n") {
					Ok(records) => println(records[0]["name"])
					Err(_) => println("error")
				}
			}
			""");
		try
		{
			var output = new StringWriter();
			var error = new StringWriter();
			TextWriter previous = Console.Out;
			Console.SetOut(output);
			int code;
			try
			{
				code = KernCli.Run(["run", directory], output, error);
			}
			finally
			{
				Console.SetOut(previous);
			}

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error.ToString());
			Assert.Equal("2\nаня\nпетров, иван\n", output.ToString().Replace("\r\n", "\n"));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Yaml_round_trips_nested_documents()
	{
		const string source = """
			fun top() -> str {
				match yaml.parse("name: kern\nversion: 1\nauthor:\n  name: Saihon\n") {
					Ok(doc) => yaml.to_str(doc)
					Err(_) => "error"
				}
			}
			fun broken() -> bool {
				match yaml.parse("key") {
					Ok(_) => false
					Err(e) => e.len > 0
				}
			}
			""";

		KValue parsed = Run(source, "top");
		Assert.Equal(ValueKind.Str, parsed.Kind);
		string text = parsed.AsStr;
		Assert.Contains("name: kern", text);
		Assert.Contains("author:", text);
		Assert.Contains("  name: Saihon", text);
		Assert.Equal(KValue.Bool(true), Run(source, "broken"));
	}

	[Fact]
	public void Crypto_hashes_and_verifies_passwords()
	{
		const string source = """
			fun sha() -> str { crypto.sha256("kern") }
			fun encoded() -> str { crypto.base64_encode("привет") }
			fun uuid_len() -> i32 { uuid.new().len }
			fun verified() -> bool {
				str hash = crypto.hash_password("секрет")
				crypto.verify_password("секрет", hash)
			}
			fun rejected() -> bool {
				str hash = crypto.hash_password("секрет")
				crypto.verify_password("другое", hash)
			}
			fun bad_base64() -> bool {
				match crypto.base64_decode("!!!") {
					Ok(_) => false
					Err(e) => e.len > 0
				}
			}
			""";

		Assert.Equal(
			KValue.Str("65e2d70166c9a802b7ad2a87129b8945f083e5f268878790a9d1f1c03f47938d"),
			Run(source, "sha"));
		Assert.Equal(KValue.Str("0L/RgNC40LLQtdGC"), Run(source, "encoded"));
		Assert.Equal(KValue.I32(36), Run(source, "uuid_len"));
		Assert.Equal(KValue.Bool(true), Run(source, "verified"));
		Assert.Equal(KValue.Bool(false), Run(source, "rejected"));
		Assert.Equal(KValue.Bool(true), Run(source, "bad_base64"));
	}

	[Fact]
	public void Xml_reads_attributes_and_finds_descendants()
	{
		const string source = """
			fun names() -> str {
				match xml.parse("<users><user id=\"1\">аня</user><user id=\"2\">борис</user></users>") {
					Ok(root) => {
						var builder = ""
						loop var user in root.find_all("user")
							builder += f"${user.attr("id")}=${user.text};"
						builder
					}
					Err(_) => "error"
				}
			}
			fun broken() -> bool {
				match xml.parse("<users>") {
					Ok(_) => false
					Err(e) => e.len > 0
				}
			}
			""";

		Assert.Equal(KValue.Str("1=аня;2=борис;"), Run(source, "names"));
		Assert.Equal(KValue.Bool(true), Run(source, "broken"));
	}

	[Fact]
	public void Regex_match_exposes_text_positions_and_groups()
	{
		const string source = """
			fun text() -> str {
				regex r = regex(r"(\d+)-(\d+)")
				match r.find_match("числа 10-20") {
					nil => "none"
					m => m.text
				}
			}
			fun group_count() -> i32 {
				regex r = regex(r"(\d+)-(\d+)")
				match r.find_match("10-20") {
					nil => -1
					m => m.groups.len
				}
			}
			fun first_group() -> str {
				regex r = regex(r"(\d+)-(\d+)")
				match r.find_match("10-20") {
					nil => "none"
					m => m.group(1) ?: "?"
				}
			}
			""";

		Assert.Equal(KValue.Str("10-20"), Run(source, "text"));
		Assert.Equal(KValue.I32(2), Run(source, "group_count"));
		Assert.Equal(KValue.Str("10"), Run(source, "first_group"));
	}

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static KValue Run(string source, string function, params KValue[] arguments) =>
		new Interpreter(Compile(source).Module, new NativeResolver(Catalog())).Run(function, arguments);

	private static CodegenResult Compile(string source)
	{
		var outcome = CompileWithDiagnostics(source);
		Assert.DoesNotContain(outcome.Diagnostics, diagnostic => diagnostic.IsError);
		Assert.NotNull(outcome.Module);
		return outcome.Module!;
	}

	private sealed record Outcome(CodegenResult? Module, IReadOnlyList<Diagnostic> Diagnostics);

	private static Outcome CompileWithDiagnostics(string source)
	{
		var diagnostics = new List<Diagnostic>();
		var parsed = Parser.Parse(source);
		diagnostics.AddRange(parsed.Diagnostics);
		if (HasErrors(diagnostics))
			return new Outcome(null, diagnostics);

		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		diagnostics.AddRange(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		diagnostics.AddRange(collectorDiagnostics);

		var catalog = Catalog();
		ModuleSymbol imported = NativeSymbolBuilder.Import(module, catalog, out var importProblems);
		Assert.Empty(importProblems);

		var bound = new Binder(imported, table).Bind();
		diagnostics.AddRange(bound.Diagnostics);
		if (HasErrors(diagnostics))
			return new Outcome(null, diagnostics);

		var generated = new CodeGenerator().Generate(bound);
		diagnostics.AddRange(generated.Diagnostics);
		return HasErrors(diagnostics)
			? new Outcome(null, diagnostics)
			: new Outcome(generated, diagnostics);
	}

	private static bool HasErrors(IReadOnlyList<Diagnostic> diagnostics) =>
		diagnostics.Any(diagnostic => diagnostic.IsError);
}
