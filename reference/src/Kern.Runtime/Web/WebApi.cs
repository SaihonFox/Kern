using System.Net;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Web;

/// <summary>Входящий запрос: метод, путь, query, заголовки, тело, параметры маршрута `/user/{id}`.</summary>
[KernType("web_request")]
public sealed class WebRequest(HttpListenerContext context, string body)
{
	internal HttpListenerContext Context { get; } = context;
	internal Dictionary<string, string> RouteParams { get; set; } = [];

	[KernProperty] public string Method => Context.Request.HttpMethod;
	[KernProperty] public string Path => Context.Request.Url?.AbsolutePath ?? "/";
	[KernProperty] public string Body { get; } = body;

	[KernMember] public string? Param(string name) => RouteParams.GetValueOrDefault(name);
	[KernMember] public string? Query(string name) => Context.Request.QueryString[name];
	[KernMember] public string? Header(string name) => Context.Request.Headers[name];
	[KernMember] public KResult<object, string> Json() => Kern.Runtime.Json.JsonApi.Parse(Body);
}

/// <summary>Ответ: статус, тело, тип содержимого, заголовки. Создаётся через `web.text/html/json/status`.</summary>
[KernType("web_response")]
public sealed class WebResponse(int status, string body, string contentType)
{
	[KernProperty] public int Status { get; set; } = status;
	[KernProperty] public string Body { get; set; } = body;
	[KernProperty] public string ContentType { get; set; } = contentType;
	public Dictionary<string, string> Headers { get; } = [];

	/// <summary>Возвращает тот же ответ с добавленным заголовком (для цепочек).</summary>
	[KernMember] public WebResponse Header(string name, string value)
	{
		Headers[name] = value;
		return this;
	}
}

/// <summary>Конструкторы ответов.</summary>
[KernType("web")]
public static class WebApi
{
	[KernStatic] public static WebResponse Text(string body) => new(200, body, "text/plain; charset=utf-8");
	[KernStatic] public static WebResponse Html(string body) => new(200, body, "text/html; charset=utf-8");
	[KernStatic] public static WebResponse Json(KValue value, [KernVm] Kern.Vm.Execution.Interpreter vm) =>
		new(200, Runtime.Json.JsonApi.ToStr(vm, value), "application/json; charset=utf-8");
	[KernStatic] public static WebResponse Status(int code, string body) => new(code, body, "text/plain; charset=utf-8");
	[KernStatic] public static WebResponse Redirect(string location) => new WebResponse(302, "", "text/plain").Header("Location", location);
	[KernStatic] public static WebResponse NotFound() => Status(404, "не найдено");
}

/// <summary>
/// HTTP-сервер на HttpListener. Обработчики выполняются в потоке VM: сеть слушается асинхронно, а Kern-код вызывается
/// только из `serve` после `await accept()`. Маршруты: `s.get("/user/{id}") fun (web_request r) -> web_response …`.
/// <code>
/// async fun main {
///     web_server s(8080)
///     s.get("/hi/{name}") fun (web_request r) -> web_response web.text(f"привет, ${r.param("name")}")
///     await s.serve()
/// }
/// </code>
/// </summary>
[KernType("web_server")]
public sealed class WebServer
{
	private readonly HttpListener _listener = new();
	private readonly List<(string Method, string[] Segments, KFunc<WebRequest, WebResponse> Handler)> _routes = [];
	private bool _started;

	[KernCtor] public WebServer(int port) : this(port, "localhost") { }

	[KernCtor] public WebServer(int port, string host)
	{
		Port = port;
		_listener.Prefixes.Add($"http://{host}:{port}/");
	}

	[KernProperty] public int Port { get; }

	private void Route(string method, string pattern, KFunc<WebRequest, WebResponse> handler) =>
		_routes.Add((method, pattern.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries), handler));

	[KernMember] public void Get(string pattern, KFunc<WebRequest, WebResponse> handler) => Route("GET", pattern, handler);
	[KernMember] public void Post(string pattern, KFunc<WebRequest, WebResponse> handler) => Route("POST", pattern, handler);
	[KernMember] public void Put(string pattern, KFunc<WebRequest, WebResponse> handler) => Route("PUT", pattern, handler);
	[KernMember] public void Delete(string pattern, KFunc<WebRequest, WebResponse> handler) => Route("DELETE", pattern, handler);

	/// <summary>Ждёт следующий запрос; nil — сервер остановлен.</summary>
	[KernMember] public async Task<WebRequest?> Accept()
	{
		if (!_started) { _listener.Start(); _started = true; }
		try
		{
			var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
			using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
			string body = await reader.ReadToEndAsync().ConfigureAwait(false);
			return new WebRequest(ctx, body);
		}
		catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return null; }
	}

	/// <summary>Находит маршрут, вызывает обработчик (в потоке VM) и отправляет ответ.</summary>
	[KernMember] public void Handle(WebRequest request)
	{
		WebResponse response;
		var segs = request.Path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		var match = _routes.FirstOrDefault(r => r.Method == request.Method && Matches(r.Segments, segs, out _));
		if (match.Handler.Inner is null) response = WebApi.NotFound();
		else
		{
			Matches(match.Segments, segs, out var prm);
			request.RouteParams = prm;
			try { response = (WebResponse)match.Handler.Invoke(KValue.Obj(request)).Ref!; }
			catch (Kern.Vm.Execution.KernPanic p) { response = WebApi.Status(500, "ошибка сервера: " + p.Message); }
		}
		Send(request.Context, response);
	}

	private static bool Matches(string[] pattern, string[] actual, out Dictionary<string, string> prm)
	{
		prm = [];
		if (pattern.Length != actual.Length) return false;
		for (int i = 0; i < pattern.Length; i++)
		{
			if (pattern[i].Length > 2 && pattern[i][0] == '{' && pattern[i][^1] == '}') prm[pattern[i][1..^1]] = Uri.UnescapeDataString(actual[i]);
			else if (pattern[i] != actual[i]) return false;
		}
		return true;
	}

	private static void Send(HttpListenerContext ctx, WebResponse r)
	{
		var bytes = Encoding.UTF8.GetBytes(r.Body);
		ctx.Response.StatusCode = r.Status;
		ctx.Response.ContentType = r.ContentType;
		foreach (var (k, v) in r.Headers) ctx.Response.Headers[k] = v;
		ctx.Response.ContentLength64 = bytes.Length;
		ctx.Response.OutputStream.Write(bytes);
		ctx.Response.Close();
	}

	[KernMember] public void Stop()
	{
		if (_started) _listener.Stop();
		_started = false;
	}
}
