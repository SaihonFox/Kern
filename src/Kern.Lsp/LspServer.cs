using System.Text.Json;
using System.Text.Json.Nodes;
using Kern.Compiler.Compilation;
using Kern.Core.Diagnostics;
using Kern.Lsp.Analysis;
using Kern.Lsp.Features;
using Kern.Lsp.Protocol;

namespace Kern.Lsp;

/// <summary>
/// Обработчик LSP: JSON-RPC методы, открытые документы и публикация диагностики.
/// Транспорт (stdio) снаружи; <see cref="Handle"/> чистый и тестируемый.
/// </summary>
public sealed class LspServer
{
	private readonly Action<string> _send;
	private readonly AnalysisService _analysis;
	private readonly DocumentStore _documents = new();

	public LspServer(Action<string> send, AnalysisService? analysis = null)
	{
		ArgumentNullException.ThrowIfNull(send);
		_send = send;
		_analysis = analysis ?? new AnalysisService();
	}

	public bool ExitRequested { get; private set; }

	public DocumentStore Documents => _documents;

	/// <summary>
	/// Обработать одно сообщение. Возвращает JSON-ответ для запроса или <c>null</c>
	/// для уведомления (уведомления самого сервера уходят через <c>send</c>).
	/// </summary>
	public string? Handle(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
			return null;

		JsonNode? parsed;
		try
		{
			parsed = JsonNode.Parse(message);
		}
		catch (JsonException)
		{
			return Error(null, -32700, "неверный JSON");
		}

		if (parsed is not JsonObject request || request["method"] is not { } methodNode)
			return null;
		string method = methodNode.GetValue<string>();
		JsonNode? id = request["id"];
		JsonNode? parameters = request["params"];

		if (id is null)
		{
			HandleNotification(method, parameters);
			return null;
		}

		try
		{
			object? result = Dispatch(method, parameters);
			return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
		}
		catch (LspException exception)
		{
			return Error(id, exception.Code, exception.Message);
		}
		catch (Exception exception)
		{
			return Error(id, -32603, "внутренняя ошибка: " + exception.Message);
		}
	}

	private object? Dispatch(string method, JsonNode? parameters) => method switch
	{
		"initialize" => Initialize(parameters),
		"shutdown" => null,
		"textDocument/completion" => Completion(parameters),
		"textDocument/hover" => Hover(parameters),
		"textDocument/signatureHelp" => SignatureHelp(parameters),
		"textDocument/definition" => Definition(parameters),
		"textDocument/documentSymbol" => DocumentSymbols(parameters),
		"textDocument/formatting" => Formatting(parameters),
		_ => throw new LspException(-32601, "метод не поддержан: " + method),
	};

	private void HandleNotification(string method, JsonNode? parameters)
	{
		switch (method)
		{
			case "initialized":
			case "$/cancelRequest":
				break;
			case "textDocument/didOpen":
				{
					JsonNode? document = parameters?["textDocument"];
					string? uri = document?["uri"]?.GetValue<string>();
					string? text = document?["text"]?.GetValue<string>();
					if (uri is not null && text is not null)
					{
						_documents.Open(uri, text);
						PublishDiagnostics();
					}

					break;
				}
			case "textDocument/didChange":
				{
					JsonNode? changes = parameters?["contentChanges"];
					string? uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
					string? text = changes is JsonArray { Count: > 0 } array
						? array[^1]?["text"]?.GetValue<string>()
						: null;
					if (uri is not null && text is not null)
					{
						_documents.Change(uri, text);
						PublishDiagnostics();
					}

					break;
				}
			case "textDocument/didClose":
				{
					string? uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
					if (uri is not null)
					{
						_documents.Close(uri);
						_send(SerializeNotification("textDocument/publishDiagnostics", new
						{
							uri,
							diagnostics = Array.Empty<object>(),
						}));
					}

					break;
				}
			case "exit":
				ExitRequested = true;
				break;
		}
	}

	private object Initialize(JsonNode? parameters)
	{
		string? root =
			parameters?["workspaceFolders"] is JsonArray { Count: > 0 } folders
				? folders[0]?["uri"]?.GetValue<string>()
				: parameters?["rootUri"]?.GetValue<string>();
		_documents.SetRoot(root);
		_analysis.SetRoot(_documents.RootPath);
		return new
		{
			capabilities = new
			{
				textDocumentSync = new { openClose = true, change = 1 },
				completionProvider = new { triggerCharacters = new[] { ".", "[" } },
				signatureHelpProvider = new { triggerCharacters = new[] { "(", "," } },
				hoverProvider = true,
				definitionProvider = true,
				documentFormattingProvider = true,
				documentSymbolProvider = true,
			},
			serverInfo = new { name = "kern-lsp", version = "0.1.0" },
		};
	}

	private object? Completion(JsonNode? parameters)
	{
		string text = RequireText(parameters);
		return new
		{
			isIncomplete = false,
			items = LspFeatures.Completion(text, Offset(parameters, text), _analysis.Index),
		};
	}

	private object? Hover(JsonNode? parameters)
	{
		string text = RequireText(parameters);
		return LspFeatures.Hover(text, Offset(parameters, text), _analysis.Index);
	}

	private object? SignatureHelp(JsonNode? parameters)
	{
		string text = RequireText(parameters);
		return LspFeatures.SignatureHelp(text, Offset(parameters, text), _analysis.Index);
	}

	private object? Definition(JsonNode? parameters)
	{
		string uri = RequireUri(parameters);
		string text = RequireText(parameters);
		return LspFeatures.Definition(uri, text, Offset(parameters, text));
	}

	private object DocumentSymbols(JsonNode? parameters) =>
		LspFeatures.Symbols(RequireText(parameters));

	private object Formatting(JsonNode? parameters) =>
		LspFeatures.Formatting(RequireText(parameters));

	private string RequireUri(JsonNode? parameters) =>
		parameters?["textDocument"]?["uri"]?.GetValue<string>()
		?? throw new LspException(-32602, "не передан textDocument.uri");

	private string RequireText(JsonNode? parameters)
	{
		string uri = RequireUri(parameters);
		return _documents.Text(uri)
			?? throw new LspException(-32602, "документ не открыт: " + uri);
	}

	private static int Offset(JsonNode? parameters, string text)
	{
		JsonNode? position = parameters?["position"];
		int line = position?["line"]?.GetValue<int>() ?? 0;
		int character = position?["character"]?.GetValue<int>() ?? 0;
		return new TextMap(text).Offset(line, character);
	}

	private void PublishDiagnostics()
	{
		IReadOnlyList<ProjectDiagnostic> diagnostics = _analysis.Analyze(_documents);
		foreach (string uri in _documents.Uris)
		{
			string? text = _documents.Text(uri);
			if (text is null)
				continue;
			string relative = _documents.RelativePath(uri);
			var map = new TextMap(text);
			var items = diagnostics
				.Where(item => string.Equals(item.Path.Replace('\\', '/'), relative, StringComparison.OrdinalIgnoreCase))
				.Select(item => ToLsp(item.Diagnostic, map))
				.ToList();
			_send(SerializeNotification("textDocument/publishDiagnostics", new
			{
				uri,
				diagnostics = items,
			}));
		}
	}

	private static object ToLsp(Diagnostic diagnostic, TextMap map) => new
	{
		range = map.Range(diagnostic.Span.Start, diagnostic.Span.Length),
		severity = diagnostic.IsError ? 1 : 2,
		code = diagnostic.Code,
		source = "kern",
		message = diagnostic.Message,
	};

	private static string SerializeNotification(string method, object parameters) =>
		JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });

	private static string Error(JsonNode? id, int code, string message) =>
		JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			id,
			error = new { code, message },
		});
}

/// <summary>Ошибка JSON-RPC (код и русский текст).</summary>
public sealed class LspException(int code, string message) : Exception(message)
{
	public int Code { get; } = code;
}
