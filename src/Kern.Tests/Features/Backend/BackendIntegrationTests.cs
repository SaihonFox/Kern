using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Kern.Tests.Features.Backend;

/// <summary>
/// Интеграционные тесты настоящего сервера: маршруты, CORS, контроллеры, DI,
/// JWT/роли, сессии, WebSocket и SSE (только loopback).
/// </summary>
[Collection("timing")]
public sealed class BackendIntegrationTests
{
	[Fact]
	public async Task Routes_middleware_and_responses_work()
	{
		using BackendTestServer server = BackendTestServer.Start(port => $$"""
			use kern.backend[app, ctx, res, middleware]
			use kern.backend.server.*

			unit point(i32 x, i32 y)

			fun main {
				app a({{port}})
				a.use(middleware.cors())
				a.get("/health", fun (ctx c) -> res res.text("ok"))
				a.get("/text", fun (ctx c) -> res res.text("hello"))
				a.get("/json", fun (ctx c) -> res res.json(point(1, 2)))
				a.get("/param/{id}", fun (ctx c) -> res res.text(c.param("id") ?: "?"))
				a.post("/echo", fun (ctx c) -> res res.text(c.body))
				a.get("/stream", fun (ctx c) -> res res.stream("text/plain", fun (res_writer w) {
					w.write("a")
					w.write_line("b")
				}))
				a.get("/stop", fun (ctx c) -> res { a.stop(); res.text("bye") })
				_ = a.serve()
			}
			""");
		using HttpClient client = server.CreateClient();

		using HttpResponseMessage health = await client.GetAsync("/health");
		Assert.Equal(HttpStatusCode.OK, health.StatusCode);
		Assert.Equal("ok", await health.Content.ReadAsStringAsync());
		Assert.Equal("*", health.Headers.GetValues("Access-Control-Allow-Origin").Single());

		using HttpResponseMessage text = await client.GetAsync("/text");
		Assert.Equal("hello", await text.Content.ReadAsStringAsync());

		using HttpResponseMessage json = await client.GetAsync("/json");
		Assert.Equal("application/json", json.Content.Headers.ContentType?.MediaType);
		using JsonDocument document = JsonDocument.Parse(await json.Content.ReadAsStringAsync());
		Assert.Equal(1, document.RootElement.GetProperty("x").GetInt32());
		Assert.Equal(2, document.RootElement.GetProperty("y").GetInt32());

		using HttpResponseMessage parameter = await client.GetAsync("/param/42");
		Assert.Equal("42", await parameter.Content.ReadAsStringAsync());

		using HttpResponseMessage stream = await client.GetAsync("/stream");
		Assert.Equal("ab\n", await stream.Content.ReadAsStringAsync());

		using var echo = new HttpRequestMessage(HttpMethod.Post, "/echo")
		{
			Content = new StringContent("payload", Encoding.UTF8, "text/plain"),
		};
		using HttpResponseMessage echoed = await client.SendAsync(echo);
		Assert.Equal("payload", await echoed.Content.ReadAsStringAsync());

		using HttpResponseMessage missing = await client.GetAsync("/missing");
		Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

		using HttpResponseMessage wrongMethod = await client.PostAsync("/text", new StringContent(""));
		Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);

		using var options = new HttpRequestMessage(HttpMethod.Options, "/text");
		using HttpResponseMessage preflight = await client.SendAsync(options);
		Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
		Assert.Equal("*", preflight.Headers.GetValues("Access-Control-Allow-Origin").Single());
	}

	[Fact]
	public async Task Controllers_di_jwt_and_roles_work()
	{
		using BackendTestServer server = BackendTestServer.Start(port => $$"""
			use kern.backend[app, ctx, res, middleware, jwt, session, claims]
			use kern.backend.server.*
			use kern.di[container]

			unit item(i64 id, str title)
			unit item_dto(str title)
			unit token_dto(str token)

			type item_repo {
				list<item> items = <item>[]
				i64 next_id = 1

				fun all() -> list<item> items

				fun add(str title) -> item {
					var created = item(next_id, title)
					next_id = next_id + 1
					items = items + [created]
					created
				}
			}

			#[controller("/api/items")]
			type item_controller(item_repo repo) {
				#[get("/")]
				fun list() -> list<item> repo.all()

				#[post("/")]
				#[auth]
				fun create(item_dto dto) -> item repo.add(dto.title)

				#[get("/who")]
				fun who(ctx c) -> str c.user ?: "none"

				#[get("/search")]
				fun search(str? q) -> str q ?: "none"

				#[get("/result-ok")]
				fun result_ok() -> result<str, str> { Ok("ok") }

				#[get("/result-err")]
				fun result_err() -> result<str, str> { Err("bad") }

				#[get("/maybe")]
				fun maybe(str? q) -> str? q

				#[get("/session")]
				fun session_value(session s) -> str s.get("user") ?: "none"

				#[get("/claims")]
				fun claim_value(claims cl) -> str cl.sub

				#[get("/admin")]
				#[roles("admin")]
				fun admin() -> str "admin"
			}

			fun main {
				var k = container()
				k.singleton<item_repo>()

				app a({{port}})
				a.services(k)
				a.use(middleware.jwt_optional("secret"))
				a.use(middleware.sessions())
				a.get("/health", fun (ctx c) -> res res.text("ok"))
				a.get("/token", fun (ctx c) -> res res.json(token_dto(jwt.sign("admin", ["admin"], "secret", 3600))))
				a.get("/token-user", fun (ctx c) -> res res.json(token_dto(jwt.sign("bob", ["user"], "secret", 3600))))
				a.get("/login-session", fun (ctx c) -> res {
					c.session!.set("user", "cookie-user")
					res.text("ok")
				})
				var secure = a.group("/secure")
				secure.use(middleware.jwt_auth("secret"))
				secure.get("/me", fun (ctx c) -> res res.text(c.user ?: "none"))
				a.scan_controllers()
				a.get("/stop", fun (ctx c) -> res { a.stop(); res.text("bye") })
				_ = a.serve()
			}
			""");
		using HttpClient client = server.CreateClient();

		using HttpResponseMessage empty = await client.GetAsync("/api/items");
		Assert.Equal("[]", await empty.Content.ReadAsStringAsync());

		using HttpResponseMessage unauthorized = await client.PostAsync(
			"/api/items",
			new StringContent("""{"title":"first"}""", Encoding.UTF8, "application/json"));
		Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

		string adminToken = await Token(client, "/token");
		string userToken = await Token(client, "/token-user");

		using var create = new HttpRequestMessage(HttpMethod.Post, "/api/items")
		{
			Content = new StringContent("""{"title":"first"}""", Encoding.UTF8, "application/json"),
		};
		create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
		using HttpResponseMessage created = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.OK, created.StatusCode);
		using JsonDocument item = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
		Assert.Equal(1, item.RootElement.GetProperty("id").GetInt64());
		Assert.Equal("first", item.RootElement.GetProperty("title").GetString());

		using HttpResponseMessage list = await client.GetAsync("/api/items");
		Assert.Contains("\"title\":\"first\"", await list.Content.ReadAsStringAsync());

		using var who = new HttpRequestMessage(HttpMethod.Get, "/api/items/who");
		who.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
		using HttpResponseMessage whoResponse = await client.SendAsync(who);
		Assert.Equal("admin", await whoResponse.Content.ReadAsStringAsync());

		using HttpResponseMessage anonymousWho = await client.GetAsync("/api/items/who");
		Assert.Equal("none", await anonymousWho.Content.ReadAsStringAsync());

		using HttpResponseMessage search = await client.GetAsync("/api/items/search?q=hello");
		Assert.Equal("hello", await search.Content.ReadAsStringAsync());

		using HttpResponseMessage emptySearch = await client.GetAsync("/api/items/search");
		Assert.Equal("none", await emptySearch.Content.ReadAsStringAsync());

		using HttpResponseMessage resultOk = await client.GetAsync("/api/items/result-ok");
		Assert.Equal("ok", await resultOk.Content.ReadAsStringAsync());

		using HttpResponseMessage resultErr = await client.GetAsync("/api/items/result-err");
		Assert.Equal(HttpStatusCode.BadRequest, resultErr.StatusCode);
		Assert.Contains("\"error\"", await resultErr.Content.ReadAsStringAsync());

		using HttpResponseMessage maybeMissing = await client.GetAsync("/api/items/maybe");
		Assert.Equal(HttpStatusCode.NotFound, maybeMissing.StatusCode);

		using HttpResponseMessage maybePresent = await client.GetAsync("/api/items/maybe?q=yes");
		Assert.Equal("yes", await maybePresent.Content.ReadAsStringAsync());

		using var admin = new HttpRequestMessage(HttpMethod.Get, "/api/items/admin");
		admin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
		using HttpResponseMessage adminResponse = await client.SendAsync(admin);
		Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);

		using var denied = new HttpRequestMessage(HttpMethod.Get, "/api/items/admin");
		denied.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
		using HttpResponseMessage deniedResponse = await client.SendAsync(denied);
		Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);

		using HttpClient cookieClient = server.CreateClient(cookies: true);
		Assert.Equal(
			"none",
			await (await cookieClient.GetAsync("/api/items/session")).Content.ReadAsStringAsync());
		Assert.Equal(
			"ok",
			await (await cookieClient.GetAsync("/login-session")).Content.ReadAsStringAsync());
		Assert.Equal(
			"cookie-user",
			await (await cookieClient.GetAsync("/api/items/session")).Content.ReadAsStringAsync());

		using var claimsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/items/claims");
		claimsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
		using HttpResponseMessage claimsResponse = await client.SendAsync(claimsRequest);
		Assert.Equal("admin", await claimsResponse.Content.ReadAsStringAsync());

		using HttpResponseMessage secureWithoutToken = await client.GetAsync("/secure/me");
		Assert.Equal(HttpStatusCode.Unauthorized, secureWithoutToken.StatusCode);

		using var secureRequest = new HttpRequestMessage(HttpMethod.Get, "/secure/me");
		secureRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
		using HttpResponseMessage secure = await client.SendAsync(secureRequest);
		Assert.Equal("admin", await secure.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Basic_bearer_and_rate_limit_middleware_work()
	{
		using BackendTestServer server = BackendTestServer.Start(port => $$"""
			use kern.backend[app, ctx, res, middleware]
			use kern.backend.server.*

			fun main {
				app a({{port}})
				a.get("/health", fun (ctx c) -> res res.text("ok"))

				var basic = a.group("/basic")
				basic.use(middleware.basic("user", "pass"))
				basic.get("/", fun (ctx c) -> res res.text(c.user ?: "none"))

				var bearer = a.group("/bearer")
				bearer.use(middleware.bearer("token"))
				bearer.get("/", fun (ctx c) -> res res.text(c.user ?: "none"))

				var limited = a.group("/limited")
				limited.use(middleware.rate_limit(2, 60))
				limited.get("/", fun (ctx c) -> res res.text("ok"))

				a.get("/stop", fun (ctx c) -> res { a.stop(); res.text("bye") })
				_ = a.serve()
			}
			""");
		using HttpClient client = server.CreateClient();

		using HttpResponseMessage basicDenied = await client.GetAsync("/basic/");
		Assert.Equal(HttpStatusCode.Unauthorized, basicDenied.StatusCode);

		using var basicRequest = new HttpRequestMessage(HttpMethod.Get, "/basic/");
		basicRequest.Headers.Authorization = new AuthenticationHeaderValue(
			"Basic",
			Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass")));
		using HttpResponseMessage basicAllowed = await client.SendAsync(basicRequest);
		Assert.Equal("user", await basicAllowed.Content.ReadAsStringAsync());

		using HttpResponseMessage bearerDenied = await client.GetAsync("/bearer/");
		Assert.Equal(HttpStatusCode.Unauthorized, bearerDenied.StatusCode);

		using var bearerRequest = new HttpRequestMessage(HttpMethod.Get, "/bearer/");
		bearerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");
		using HttpResponseMessage bearerAllowed = await client.SendAsync(bearerRequest);
		Assert.Equal("bearer", await bearerAllowed.Content.ReadAsStringAsync());

		using HttpResponseMessage first = await client.GetAsync("/limited/");
		using HttpResponseMessage second = await client.GetAsync("/limited/");
		using HttpResponseMessage third = await client.GetAsync("/limited/");
		Assert.Equal(HttpStatusCode.OK, first.StatusCode);
		Assert.Equal(HttpStatusCode.OK, second.StatusCode);
		Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
	}

	[Fact]
	public async Task Sessions_survive_across_requests()
	{
		using BackendTestServer server = BackendTestServer.Start(port => $$"""
			use kern.backend[app, ctx, res, middleware]
			use kern.backend.server.*

			fun main {
				app a({{port}})
				a.use(middleware.sessions())
				a.get("/health", fun (ctx c) -> res res.text("ok"))
				a.get("/login", fun (ctx c) -> res {
					c.session!.set("user", "anya")
					res.text("ok")
				})
				a.get("/me", fun (ctx c) -> res res.text(c.session!.get("user") ?: "none"))
				a.get("/stop", fun (ctx c) -> res { a.stop(); res.text("bye") })
				_ = a.serve()
			}
			""");
		using HttpClient cookieClient = server.CreateClient(cookies: true);
		using HttpClient anonymous = server.CreateClient();

		Assert.Equal("none", await (await cookieClient.GetAsync("/me")).Content.ReadAsStringAsync());
		Assert.Equal("ok", await (await cookieClient.GetAsync("/login")).Content.ReadAsStringAsync());
		Assert.Equal("anya", await (await cookieClient.GetAsync("/me")).Content.ReadAsStringAsync());
		Assert.Equal("none", await (await anonymous.GetAsync("/me")).Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task WebSocket_echo_and_sse_stream_work()
	{
		using BackendTestServer server = BackendTestServer.Start(port => $$"""
			use kern.backend[app, ctx, res, socket, sse]
			use kern.backend.server.*

			fun main {
				app a({{port}})
				a.get("/health", fun (ctx c) -> res res.text("ok"))
				a.ws("/echo", fun (socket s, str text) { s.send(f"echo:${text}") })
				a.sse("/events", fun (sse s) { s.send("hello", "world") })
				a.post("/notify", fun (ctx c) -> res res.text(f"${a.sse_broadcast("/events", "note", "broadcast")}"))
				a.post("/ws-notify", fun (ctx c) -> res res.text(f"${a.ws_broadcast("/echo", "all")}"))
				a.get("/stop", fun (ctx c) -> res { a.stop(); res.text("bye") })
				_ = a.serve()
			}
			""");

		using HttpClient sseClient = server.CreateClient();
		using HttpResponseMessage plainEcho = await sseClient.GetAsync("/echo");
		Assert.Equal(HttpStatusCode.UpgradeRequired, plainEcho.StatusCode);
		Assert.Equal("websocket", plainEcho.Headers.GetValues("Upgrade").Single());
		Assert.Contains(
			"WebSocket",
			await plainEcho.Content.ReadAsStringAsync(),
			StringComparison.Ordinal);

		using HttpResponseMessage sse = await sseClient.GetAsync("/events", HttpCompletionOption.ResponseHeadersRead);
		await using Stream stream = await sse.Content.ReadAsStreamAsync();
		using var reader = new StreamReader(stream);
		var lines = new List<string>();
		while (lines.Count < 8)
		{
			string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
			if (line is null)
				break;
			lines.Add(line);
			if (line == "data: world")
				break;
		}

		Assert.Contains("event: hello", lines);
		Assert.Contains("data: world", lines);

		using HttpResponseMessage notify = await sseClient.PostAsync("/notify", new StringContent(""));
		Assert.Equal("1", await notify.Content.ReadAsStringAsync());
		while (lines.Count < 16)
		{
			string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
			if (line is null)
				break;
			lines.Add(line);
			if (line == "data: broadcast")
				break;
		}

		Assert.Contains("event: note", lines);
		Assert.Contains("data: broadcast", lines);

		using var replayRequest = new HttpRequestMessage(HttpMethod.Get, "/events");
		replayRequest.Headers.TryAddWithoutValidation("Last-Event-ID", "1");
		using HttpResponseMessage replay = await sseClient.SendAsync(
			replayRequest,
			HttpCompletionOption.ResponseHeadersRead);
		await using Stream replayStream = await replay.Content.ReadAsStreamAsync();
		using var replayReader = new StreamReader(replayStream);
		var replayLines = new List<string>();
		while (replayLines.Count < 16)
		{
			string? line = await replayReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
			if (line is null)
				break;
			replayLines.Add(line);
			if (line == "data: broadcast")
				break;
		}

		Assert.Contains("event: note", replayLines);
		Assert.Contains("data: broadcast", replayLines);

		using var socket = new ClientWebSocket();
		await socket.ConnectAsync(new Uri($"ws://localhost:{server.Port}/echo"), CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(5));
		using HttpResponseMessage wsNotify = await sseClient.PostAsync("/ws-notify", new StringContent(""));
		Assert.Equal("1", await wsNotify.Content.ReadAsStringAsync());
		var buffer = new byte[256];
		WebSocketReceiveResult broadcast = await socket.ReceiveAsync(buffer, CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal("all", Encoding.UTF8.GetString(buffer, 0, broadcast.Count));

		await socket.SendAsync(
				Encoding.UTF8.GetBytes("hi"),
				WebSocketMessageType.Text,
				true,
				CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(5));
		WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal("echo:hi", Encoding.UTF8.GetString(buffer, 0, received.Count));
		await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task Static_files_serve_index_and_files()
	{
		string root = Path.Combine(Path.GetTempPath(), $"kern-backend-static-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		System.IO.File.WriteAllText(Path.Combine(root, "index.html"), "index", new UTF8Encoding(false));
		System.IO.File.WriteAllText(Path.Combine(root, "app.js"), "js", new UTF8Encoding(false));
		string kernRoot = root.Replace('\\', '/');
		try
		{
			using BackendTestServer server = BackendTestServer.Start(port => $$"""
				use kern.backend[app, ctx, res]
				use kern.backend.server.*

				fun main {
					app a({{port}})
					a.get("/health", fun (ctx c) -> res res.text("ok"))
					a.static_files("/assets", "{{kernRoot}}")
					a.get("/stop", fun (ctx c) -> res { a.stop(); res.text("bye") })
					_ = a.serve()
				}
				""");
			using HttpClient client = server.CreateClient();

			using HttpResponseMessage index = await client.GetAsync("/assets/");
			Assert.Equal("index", await index.Content.ReadAsStringAsync());

			using HttpResponseMessage script = await client.GetAsync("/assets/app.js");
			Assert.Equal("js", await script.Content.ReadAsStringAsync());
			Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
			string etag = script.Headers.GetValues("ETag").Single();
			Assert.True(script.Content.Headers.Contains("Last-Modified"));

			using var cachedRequest = new HttpRequestMessage(HttpMethod.Get, "/assets/app.js");
			cachedRequest.Headers.TryAddWithoutValidation("If-None-Match", etag);
			using HttpResponseMessage cached = await client.SendAsync(cachedRequest);
			Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
			Assert.Empty(await cached.Content.ReadAsByteArrayAsync());

			using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "/assets/app.js");
			rangeRequest.Headers.TryAddWithoutValidation("Range", "bytes=0-1");
			using HttpResponseMessage range = await client.SendAsync(rangeRequest);
			Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
			Assert.Equal("js", await range.Content.ReadAsStringAsync());
			Assert.Equal("bytes 0-1/2", range.Content.Headers.GetValues("Content-Range").Single());

			using var suffixRequest = new HttpRequestMessage(HttpMethod.Get, "/assets/app.js");
			suffixRequest.Headers.TryAddWithoutValidation("Range", "bytes=-1");
			using HttpResponseMessage suffix = await client.SendAsync(suffixRequest);
			Assert.Equal("s", await suffix.Content.ReadAsStringAsync());

			using HttpResponseMessage missing = await client.GetAsync("/assets/missing.txt");
			Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Server_module_free_serve_function_works()
	{
		using BackendTestServer server = BackendTestServer.Start(port => $$"""
			use kern.backend[app, ctx, res]
			use kern.backend.server.*

			async fun main {
				app a({{port}})
				a.get("/health", fun (ctx c) -> res res.text("ok"))
				a.get("/stop", fun (ctx c) -> res { a.stop(); res.text("bye") })
				await serve(a)
			}
			""");
		using HttpClient client = server.CreateClient();

		using HttpResponseMessage health = await client.GetAsync("/health");
		Assert.Equal("ok", await health.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Manual_accept_and_handle_loop_works()
	{
		using BackendTestServer server = BackendTestServer.Start(port => $$"""
			use kern.backend[app, ctx, res, work]
			use kern.backend.server.*

			async fun main {
				app a({{port}})
				a.get("/health", fun (ctx c) -> res res.text("ok"))
				a.get("/value", fun (ctx c) -> res res.text("manual"))
				a.get("/stop", fun (ctx c) -> res { a.stop(); res.text("bye") })
				loop {
					var item = await a.accept()
					if item == nil { stop }
					a.handle(item!)
				}
			}
			""");
		using HttpClient client = server.CreateClient();

		using HttpResponseMessage value = await client.GetAsync("/value");
		Assert.Equal("manual", await value.Content.ReadAsStringAsync());
	}

	private static async Task<string> Token(HttpClient client, string path)
	{
		using HttpResponseMessage response = await client.GetAsync(path);
		response.EnsureSuccessStatusCode();
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("token").GetString()!;
	}
}
