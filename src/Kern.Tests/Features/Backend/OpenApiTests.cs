using System.Net;
using System.Text.Json;
using Xunit;

namespace Kern.Tests.Features.Backend;

/// <summary>OpenAPI: документ по HTTP-маршрутам, строка и HTML-страница.</summary>
[Collection("timing")]
public sealed class OpenApiTests
{
	private const string Source = """
		use kern.backend[app, ctx, res]
		use kern.backend.server.*

		fun main {
			app a(__PORT__)
			a.get("/health", fun (ctx c) -> res res.text("ok"))
			a.get("/users/{id}", fun (ctx c) -> res res.text(c.param("id") ?: "?"))
			a.post("/users", fun (ctx c) -> res res.json_status(201, c.body))
			a.ws("/echo", fun (socket s, str text) {})
			a.get("/document", fun (ctx c) -> res res.text(a.openapi_document()))
			a.openapi("/openapi.json")
			a.openapi_ui("/docs")
			a.get("/stop", fun (ctx c) -> res { a.stop(); res.text("bye") })
			_ = a.serve()
		}
		""";

	[Fact]
	public async Task Openapi_document_describes_http_routes_only()
	{
		using BackendTestServer server = BackendTestServer.Start(port => Source.Replace("__PORT__", port.ToString()));
		using HttpClient client = server.CreateClient();

		using HttpResponseMessage response = await client.GetAsync("/openapi.json");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;
		Assert.Equal("3.1.0", root.GetProperty("openapi").GetString());
		Assert.Equal("Kern API", root.GetProperty("info").GetProperty("title").GetString());
		Assert.Equal("1.0.0", root.GetProperty("info").GetProperty("version").GetString());

		JsonElement paths = root.GetProperty("paths");
		Assert.True(paths.TryGetProperty("/users/{id}", out JsonElement userById));
		JsonElement get = userById.GetProperty("get");
		Assert.False(string.IsNullOrWhiteSpace(get.GetProperty("operationId").GetString()));
		JsonElement parameter = get.GetProperty("parameters")[0];
		Assert.Equal("id", parameter.GetProperty("name").GetString());
		Assert.Equal("path", parameter.GetProperty("in").GetString());
		Assert.True(parameter.GetProperty("required").GetBoolean());
		Assert.Equal("string", parameter.GetProperty("schema").GetProperty("type").GetString());
		Assert.Equal("users", get.GetProperty("tags")[0].GetString());

		Assert.True(paths.TryGetProperty("/users", out JsonElement users));
		Assert.True(users.TryGetProperty("post", out _));
		Assert.False(paths.TryGetProperty("/echo", out _));
		Assert.False(paths.TryGetProperty("/openapi.json", out _));
		Assert.True(root.TryGetProperty("components", out _));
	}

	private const string CustomSource = """
		use kern.backend[app, ctx, res]
		use kern.backend.server.*

		fun main {
			app a(__PORT__)
			a.get("/health", fun (ctx c) -> res res.text("ok"))
			a.get("/items", fun (ctx c) -> res res.text("items"))
			a.get("/files/{*path}", fun (ctx c) -> res res.text(c.param("path") ?: "?"))
			a.openapi("/spec.json", "Custom API", "2.1.0")
			a.openapi_ui("/docs", "/spec.json")
			a.get("/stop", fun (ctx c) -> res { a.stop(); res.text("bye") })
			_ = a.serve()
		}
		""";

	[Fact]
	public async Task Openapi_title_version_and_ui_json_path_are_configurable()
	{
		using BackendTestServer server = BackendTestServer.Start(
			port => CustomSource.Replace("__PORT__", port.ToString()));
		using HttpClient client = server.CreateClient();

		using HttpResponseMessage response = await client.GetAsync("/spec.json");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;
		Assert.Equal("Custom API", root.GetProperty("info").GetProperty("title").GetString());
		Assert.Equal("2.1.0", root.GetProperty("info").GetProperty("version").GetString());
		JsonElement paths = root.GetProperty("paths");
		Assert.True(paths.TryGetProperty("/items", out _));
		Assert.False(paths.TryGetProperty("/spec.json", out _));
		JsonElement files = paths.GetProperty("/files/{*path}").GetProperty("get");
		JsonElement parameter = files.GetProperty("parameters")[0];
		Assert.Equal("path", parameter.GetProperty("name").GetString());
		Assert.True(parameter.GetProperty("required").GetBoolean());

		using HttpResponseMessage ui = await client.GetAsync("/docs");
		string html = await ui.Content.ReadAsStringAsync();
		Assert.Contains("/spec.json", html);
	}

	[Fact]
	public async Task Openapi_document_member_and_ui_page_work()
	{
		using BackendTestServer server = BackendTestServer.Start(port => Source.Replace("__PORT__", port.ToString()));
		using HttpClient client = server.CreateClient();

		using HttpResponseMessage document = await client.GetAsync("/document");
		Assert.Equal(HttpStatusCode.OK, document.StatusCode);
		string text = await document.Content.ReadAsStringAsync();
		Assert.Contains("\"openapi\"", text);
		Assert.Contains("/users/{id}", text);

		using HttpResponseMessage ui = await client.GetAsync("/docs");
		Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
		Assert.Equal("text/html", ui.Content.Headers.ContentType?.MediaType);
		string html = await ui.Content.ReadAsStringAsync();
		Assert.Contains("/openapi.json", html);
		Assert.Contains("<table", html);
	}
}
