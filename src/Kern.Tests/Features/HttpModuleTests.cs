using System.Net;
using System.Net.Sockets;
using System.Text;
using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// <c>kern.http</c> (spec rounds 5/10): оба варианта типизированного клиента проверяются
/// на локальном loopback-сервере (без интернета).
/// </summary>
public sealed class HttpModuleTests
{
	[Fact]
	public void Client_get_parses_json_into_a_unit()
	{
		using var server = Loopback.Start("/posts/1", """{"id":1,"title":"hello"}""");
		string source = $$"""
			unit Post(i64 id, str title)

			async fun run() -> str {
				http_client api("{{server.BaseUrl}}")
				match await api.get<Post>("/posts/1") {
					Ok(post) => post.title
					Err(message) => message
				}
			}
			""";

		Assert.Equal(KValue.Str("hello"), StdPipeline.RunAsync(source, "run"));
	}

	[Fact]
	public void Proxy_uses_method_attributes_and_path_placeholders()
	{
		using var server = Loopback.Start("/posts/7", """{"id":7,"title":"proxy"}""");
		string source = $$"""
			unit Post(i64 id, str title)

			@type Api {
				#[get("/posts/{id}")]
				@fun one(i64 id) -> task<result<Post, str>>
			}

			async fun run() -> str {
				Api api = http_client.proxy<Api>("{{server.BaseUrl}}")
				match await api.one(7) {
					Ok(post) => post.title
					Err(message) => message
				}
			}
			""";

		Assert.Equal(KValue.Str("proxy"), StdPipeline.RunAsync(source, "run"));
	}

	[Fact]
	public void Plain_get_returns_status_and_body()
	{
		using var server = Loopback.Start("/plain", "pong");
		string source = $$"""
			async fun run() -> str {
				match await http.get("{{server.BaseUrl}}/plain") {
					Ok(reply) => f"${reply.status} ${reply.body}"
					Err(message) => message
				}
			}
			""";

		Assert.Equal(KValue.Str("200 pong"), StdPipeline.RunAsync(source, "run"));
	}

	[Fact]
	public void Non_success_status_is_err()
	{
		using var server = Loopback.Start("/missing", "no", status: 404);
		string source = $$"""
			async fun run() -> bool {
				match await http.get("{{server.BaseUrl}}/missing") {
					Ok(_) => false
					Err(message) => message.contains("404")
				}
			}
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.RunAsync(source, "run"));
	}

	/// <summary>Минимальный HTTP-сервер на <see cref="HttpListener"/> для одного пути.</summary>
	private sealed class Loopback : IDisposable
	{
		private readonly HttpListener _listener;
		private readonly CancellationTokenSource _stop = new();

		private Loopback(string baseUrl, HttpListener listener)
		{
			BaseUrl = baseUrl;
			_listener = listener;
		}

		public string BaseUrl { get; }

		public static Loopback Start(string path, string body, int status = 200)
		{
			int port = FreePort();
			string prefix = $"http://127.0.0.1:{port}/";
			var listener = new HttpListener();
			listener.Prefixes.Add(prefix);
			listener.Start();
			var loopback = new Loopback(prefix.TrimEnd('/'), listener);
			_ = Task.Run(async () =>
			{
				while (!loopback._stop.IsCancellationRequested)
				{
					HttpListenerContext context;
					try
					{
						context = await listener.GetContextAsync();
					}
					catch (Exception) when (loopback._stop.IsCancellationRequested)
					{
						return;
					}

					byte[] payload = Encoding.UTF8.GetBytes(
						context.Request.Url?.AbsolutePath == path ? body : "not found");
					context.Response.StatusCode = context.Request.Url?.AbsolutePath == path ? status : 404;
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = payload.Length;
					await context.Response.OutputStream.WriteAsync(payload);
					context.Response.Close();
				}
			});
			return loopback;
		}

		private static int FreePort()
		{
			var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			int port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();
			return port;
		}

		public void Dispose()
		{
			_stop.Cancel();
			try
			{
				_listener.Stop();
				_listener.Close();
			}
			catch (ObjectDisposedException)
			{
			}

			_stop.Dispose();
		}
	}
}
