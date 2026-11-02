using System.Text.Json;
using System.Text.Json.Nodes;
using Kern.Lsp;
using Kern.Lsp.Protocol;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// LSP MVP (spec §"Инструменты"): JSON-RPC методы, диагностика открытого документа,
/// completion/hover/definition/formatting/documentSymbol.
/// </summary>
public sealed class LspTests
{
	private const string Uri = "file:///tmp/lsp_test.kern";

	[Fact]
	public void Initialize_reports_capabilities()
	{
		var server = Create(out _);
		JsonNode? result = Call(server, 1, "initialize", new { rootUri = (string?)null });
		Assert.NotNull(result);
		Assert.True(result!["capabilities"]!["completionProvider"] is not null);
		Assert.True((bool)result["capabilities"]!["hoverProvider"]!);
		Assert.Equal("kern-lsp", (string)result["serverInfo"]!["name"]!);
	}

	[Fact]
	public void Document_symbols_list_functions_types_and_units()
	{
		var server = Create(out _);
		Notify(server, "textDocument/didOpen", Document(
			"fun foo() -> i32 { 1 }\n\ntype Point {\n\ti32 x\n}\n\nunit Red\n"));

		JsonNode? result = Call(server, 2, "textDocument/documentSymbol", DocumentId());
		var names = result!.AsArray().Select(item => (string)item!["name"]!).ToList();
		Assert.Contains("foo", names);
		Assert.Contains("Point", names);
		Assert.Contains("Red", names);
	}

	[Fact]
	public void DidOpen_publishes_diagnostics_for_a_broken_document()
	{
		var server = Create(out List<string> sent);
		Notify(server, "textDocument/didOpen", Document("fun bad() -> i32 {\n\t\"text\"\n}\n"));

		JsonNode notification = JsonNode.Parse(sent.Last())!;
		Assert.Equal("textDocument/publishDiagnostics", (string)notification["method"]!);
		JsonArray diagnostics = notification["params"]!["diagnostics"]!.AsArray();
		Assert.NotEmpty(diagnostics);
		Assert.Equal("kern", (string)diagnostics[0]!["source"]!);
	}

	[Fact]
	public void Definition_finds_a_function_declaration()
	{
		const string source = "fun foo() -> i32 { 1 }\n\nfun main {\n\tfoo()\n}\n";
		var server = Create(out _);
		Notify(server, "textDocument/didOpen", Document(source));
		var map = new TextMap(source);
		int offset = source.LastIndexOf("foo()", StringComparison.Ordinal);

		JsonNode? result = Call(server, 3, "textDocument/definition", new
		{
			textDocument = new { uri = Uri },
			position = Position(map, offset),
		});

		Assert.NotNull(result);
		Assert.Equal(Uri, (string)result!["uri"]!);
		Assert.Equal(0, (int)result["range"]!["start"]!["line"]!);
	}

	[Fact]
	public void Completion_contains_keywords_and_identifiers()
	{
		var server = Create(out _);
		Notify(server, "textDocument/didOpen", Document("fun alpha() -> i32 { 1 }\nfun main { alpha() }\n"));
		JsonNode? result = Call(server, 4, "textDocument/completion", DocumentId());
		var labels = result!["items"]!.AsArray().Select(item => (string)item!["label"]!).ToList();
		Assert.Contains("fun", labels);
		Assert.Contains("alpha", labels);
	}

	[Fact]
	public void Completion_after_dot_lists_members()
	{
		const string source = """
			type Counter {
				i32 value = 0
				fun bump() -> i32 {
					value += 1
					value
				}
			}

			fun main {
				Counter c()
				c.bump()
			}
			""";
		var server = Create(out _);
		Notify(server, "textDocument/didOpen", Document(source));
		int offset = source.IndexOf("c.bump", StringComparison.Ordinal) + 2;
		JsonNode? result = Call(server, 7, "textDocument/completion", new
		{
			textDocument = new { uri = Uri },
			position = Position(new TextMap(source), offset),
		});
		var labels = result!["items"]!.AsArray().Select(item => (string)item!["label"]!).ToList();
		Assert.Contains("bump", labels);
		Assert.Contains("value", labels);
	}

	[Fact]
	public void Signature_help_shows_parameters()
	{
		const string source = "fun add(i32 a, str b) -> i32 { a }\nfun main { add(1, \"x\") }\n";
		var server = Create(out _);
		Notify(server, "textDocument/didOpen", Document(source));
		int offset = source.IndexOf("add(", StringComparison.Ordinal) + 4;
		JsonNode? result = Call(server, 8, "textDocument/signatureHelp", new
		{
			textDocument = new { uri = Uri },
			position = Position(new TextMap(source), offset),
		});
		Assert.NotNull(result);
		string label = (string)result!["signatures"]![0]!["label"]!;
		Assert.Contains("add", label);
		Assert.Contains("i32", label);
	}

	[Fact]
	public void Imports_from_unopened_project_files_are_resolved()
	{
		string directory = Path.Combine(Path.GetTempPath(), $"kern-lsp-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Path.Combine(directory, "src"));
		string main = Path.Combine(directory, "src", "main.kern");
		File.WriteAllText(Path.Combine(directory, "src", "lib.kern"), "pub fun value() -> i32 7\n");
		File.WriteAllText(main, "use lib[value]\nfun main { println(value()) }\n");
		string uri = new Uri(main).AbsoluteUri;
		try
		{
			var sent = new List<string>();
			var server = new LspServer(sent.Add);
			Call(server, 1, "initialize", new
			{
				rootUri = new Uri(directory).AbsoluteUri,
				capabilities = new { },
			});
			Notify(server, "textDocument/didOpen", new
			{
				textDocument = new { uri, languageId = "kern", version = 1, text = File.ReadAllText(main) },
			});
			var diagnostic = sent
				.Select(message => JsonNode.Parse(message)!)
				.Last(node => (string?)node["method"] == "textDocument/publishDiagnostics");
			Assert.Empty(diagnostic["params"]!["diagnostics"]!.AsArray());
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Hover_returns_a_kern_code_block()
	{
		const string source = "fun foo() -> i32 { 1 }\n";
		var server = Create(out _);
		Notify(server, "textDocument/didOpen", Document(source));
		JsonNode? result = Call(server, 5, "textDocument/hover", new
		{
			textDocument = new { uri = Uri },
			position = Position(new TextMap(source), 4),
		});
		Assert.NotNull(result);
		Assert.Contains("```kern", (string)result!["contents"]!["value"]!);
	}

	[Fact]
	public void Formatting_returns_a_full_document_edit()
	{
		var server = Create(out _);
		Notify(server, "textDocument/didOpen", Document("fun main {\n  println(\"x\")   \n}\n"));
		JsonNode? result = Call(server, 6, "textDocument/formatting", DocumentId());
		JsonArray edits = Assert.IsType<JsonArray>(result);
		Assert.Single(edits);
		Assert.Contains("println", (string)edits[0]!["newText"]!);
	}

	private static LspServer Create(out List<string> sent)
	{
		var messages = new List<string>();
		sent = messages;
		return new LspServer(messages.Add);
	}

	private static object Document(string text) => new
	{
		textDocument = new { uri = Uri, languageId = "kern", version = 1, text },
	};

	private static object DocumentId() => new { textDocument = new { uri = Uri } };

	private static object Position(TextMap map, int offset)
	{
		var (line, character) = map.Position(offset);
		return new { line, character };
	}

	private static JsonNode? Call(LspServer server, int id, string method, object parameters)
	{
		string message = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
		string? response = server.Handle(message);
		Assert.NotNull(response);
		JsonNode root = JsonNode.Parse(response!)!;
		Assert.Null(root["error"]);
		return root["result"];
	}

	private static void Notify(LspServer server, string method, object parameters)
	{
		string message = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });
		Assert.Null(server.Handle(message));
	}
}
