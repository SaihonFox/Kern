using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Lib.Backend.Http;
using Kern.Lib.Backend.Middleware;
using Kern.Lib.Backend.Realtime;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Routing;

/// <summary>
/// HTTP-приложение: корневая группа маршрутов + сервер. Kern-код всегда исполняется в потоке VM:
/// фоновые потоки только принимают соединения и кладут работу в очередь (`accept`), а `handle` разбирает её.
/// <code>
/// async fun main {
///     app a(8080)
///     a.use(middleware.logger())
///     a.get("/hi/{name}") fun (ctx c) -> res res.text(f"привет, ${c.param("name")}")
///     await a.serve()
/// }
/// </code>
/// </summary>
[KernType("app")]
public sealed partial class KernApp : KernGroup
{
	private readonly HttpListener _listener = new();
	private readonly Channel<KernWork> _queue = Channel.CreateUnbounded<KernWork>();
	private readonly Dictionary<KernSocket, RouteEntry> _sockets = [];
	private readonly List<KernSse> _sse = [];
	private bool _started;

	internal List<RouteEntry> Routes { get; } = [];
	internal override KernApp App => this;

	[KernCtor] public KernApp() : this(8080, "localhost") { }
	[KernCtor] public KernApp(int port) : this(port, "localhost") { }

	[KernCtor]
	public KernApp(int port, string host)
	{
		Port = port;
		Host = host;
		_listener.Prefixes.Add($"http://{host}:{port}/");
	}

	[KernProperty] public int Port { get; }
	[KernProperty] public string Host { get; }
	/// <summary>true — не печатать сообщения сервера (старт, ошибки обработчиков).</summary>
	[KernProperty] public bool Quiet { get; set; }
	[KernProperty] public int Connections => _sockets.Count + _sse.Count(s => !s.Closed);

	// ------------------------------------------------------------- цикл сервера

	/// <summary>Ждёт следующую единицу работы; nil — сервер остановлен.</summary>
	[KernMember]
	public async Task<KernWork?> Accept()
	{
		Start();
		try { return await _queue.Reader.ReadAsync().ConfigureAwait(false); }
		catch (ChannelClosedException) { return null; }
	}

	/// <summary>Исполняет работу (HTTP-запрос или событие WebSocket) в потоке VM.</summary>
	[KernMember]
	public void Handle(KernWork work, [KernVm] Interpreter vm)
	{
		switch (work.Kind)
		{
			case WorkKind.Http:
				HandleHttp(work.Ctx!, vm);
				break;
			case WorkKind.WsMessage:
				if (_sockets.TryGetValue(work.Socket!, out var open))
					Guard(() => open.OnMessage?.Invoke(KValue.Obj(work.Socket!), KValue.Str(work.Text)));
				break;
			case WorkKind.WsClosed:
				if (_sockets.Remove(work.Socket!, out var closed))
					Guard(() => closed.OnClose?.Invoke(KValue.Obj(work.Socket!)));
				break;
		}
	}

	/// <summary>Останавливает сервер и закрывает все WebSocket/SSE-соединения; `serve()` завершится.</summary>
	[KernMember]
	public void Stop()
	{
		foreach (var s in _sockets.Keys.ToArray()) s.Close();
		foreach (var s in _sse.ToArray()) s.Close();
		_sockets.Clear();
		_sse.Clear();
		try { _listener.Stop(); }
		catch (ObjectDisposedException) { }
		_queue.Writer.TryComplete();
	}

	/// <summary>Отправляет событие всем SSE-подписчикам маршрута (шаблон, как в `a.sse(...)`); возвращает число получателей.</summary>
	[KernMember]
	public int SseBroadcast(string route, string eventName, string data)
	{
		_sse.RemoveAll(s => s.Closed);
		return _sse.Where(s => s.Route == route).Count(s => s.Send(eventName, data));
	}

	/// <summary>Отправляет текст всем WebSocket-клиентам маршрута; возвращает число получателей.</summary>
	[KernMember]
	public int WsBroadcast(string route, string text)
	{
		foreach (var dead in _sockets.Keys.Where(s => !s.Open).ToArray()) _sockets.Remove(dead);
		return _sockets.Where(kv => kv.Value.Template == route).Count(kv => kv.Key.Send(text));
	}

	// ------------------------------------------------------------- фон

	private void Start()
	{
		if (_started) return;
		_started = true;
		_listener.Start();
		if (!Quiet) Console.Out.WriteLine($"слушаю http://{Host}:{Port}/");
		_ = Task.Run(Pump);
	}

	private async Task Pump()
	{
		while (_listener.IsListening)
		{
			HttpListenerContext c;
			try { c = await _listener.GetContextAsync().ConfigureAwait(false); }
			catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }
			_ = Task.Run(() => Enqueue(c));
		}
		_queue.Writer.TryComplete();
	}

	private async Task Enqueue(HttpListenerContext c)
	{
		try
		{
			string body = "";
			if (!c.Request.IsWebSocketRequest && c.Request.HasEntityBody)
			{
				using var reader = new StreamReader(c.Request.InputStream, Encoding.UTF8);
				body = await reader.ReadToEndAsync().ConfigureAwait(false);
			}
			_queue.Writer.TryWrite(KernWork.Http(new KernCtx(c, body)));
		}
		catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
		{
			try { c.Response.Abort(); } catch (ObjectDisposedException) { }
		}
	}

	// ------------------------------------------------------------- HTTP

	private void HandleHttp(KernCtx ctx, Interpreter vm)
	{
		ctx.Vm = vm;
		KernRes res;
		try { res = Dispatch(ctx); }
		catch (KernPanic p) { res = KernRes.Error(500, "ошибка сервера: " + p.Message); }

		if (res.Takeover is { } takeover)
		{
			try { takeover(vm); return; }
			catch (Exception e) when (e is KernPanic or WebSocketException or HttpListenerException or InvalidOperationException)
			{
				res = KernRes.Error(500, "ошибка сервера: " + e.Message);
			}
		}
		Send(ctx.Raw.Response, res);
	}

	private KernRes Dispatch(KernCtx ctx)
	{
		bool isWs = ctx.Raw.Request.IsWebSocketRequest;
		RouteEntry? best = null;
		Dictionary<string, string> bestParams = [];
		bool pathMatched = false;
		foreach (var r in Routes)
		{
			if (!r.Pattern.Match(ctx.Path, out var values)) continue;
			pathMatched = true;
			bool ok = r.Kind switch
			{
				RouteKind.Http => r.Method == ctx.Method || (ctx.Method == "HEAD" && r.Method == "GET"),
				RouteKind.WebSocket => isWs,
				_ => ctx.Method == "GET" && !isWs,
			};
			if (!ok || (best is not null && r.Pattern.Specificity <= best.Pattern.Specificity)) continue;
			best = r;
			bestParams = values;
		}

		ctx.RouteParams = bestParams;
		if (best is null)
		{
			var miss = pathMatched && ctx.Method != "OPTIONS" ? KernRes.Error(405, "метод не поддерживается") : KernRes.NotFound();
			return new KernChain(CollectLayers(), _ => miss, ctx).Next();
		}
		List<Layer> layers = [.. best.Group.CollectLayers(), .. best.Extra];
		return new KernChain(layers, best.Terminal, ctx).Next();
	}

	private static void Send(HttpListenerResponse response, KernRes res)
	{
		try
		{
			response.StatusCode = res.Status;
			response.ContentType = res.ContentType;
			foreach (var (k, v) in res.Headers) response.Headers[k] = v;
			foreach (var c in res.Cookies) response.Headers.Add("Set-Cookie", c);
			response.ContentLength64 = res.Bytes.Length;
			response.OutputStream.Write(res.Bytes, 0, res.Bytes.Length);
			response.Close();
		}
		catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException) { }
	}

	// ------------------------------------------------------------- WebSocket / SSE

	internal void OpenSocket(KernCtx ctx, RouteEntry route, Interpreter vm)
	{
		var wsCtx = ctx.Raw.AcceptWebSocketAsync(null).GetAwaiter().GetResult();
		var socket = new KernSocket(wsCtx.WebSocket, ctx.Raw.Request, route.Template);
		_sockets[socket] = route;
		Guard(() => route.OnOpen?.Invoke(KValue.Obj(socket)));
		_ = socket.ReadLoop(w => _queue.Writer.TryWrite(w));
	}

	internal void OpenSse(KernCtx ctx, string template, KAction<KernSse> onOpen, Interpreter vm)
	{
		var sse = new KernSse(ctx.Raw.Response, template);
		sse.Ping();
		_sse.Add(sse);
		Guard(() => onOpen.Invoke(KValue.Obj(sse)));
	}

	/// <summary>Ошибка в обработчике соединения не должна ронять сервер.</summary>
	private void Guard(Action action)
	{
		try { action(); }
		catch (KernPanic p) { if (!Quiet) Console.Error.WriteLine("ошибка в обработчике: " + p.Message); }
	}
}
