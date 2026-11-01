using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Lib.Backend.Http;
using Kern.Lib.Backend.Middleware;
using Kern.Lib.Backend.Realtime;
using Kern.Lib.Di;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Routing;

/// <summary>
/// HTTP-сервер: маршруты, middleware, контроллеры, WebSocket и SSE. Цикл запросов
/// выполняется в потоке VM через <c>await a.serve()</c>.
/// </summary>
[KernType("app")]
public sealed partial class KernApp : KernGroup
{
	private readonly HttpListener _listener = new();
	private readonly Channel<KernWork> _queue = Channel.CreateUnbounded<KernWork>();
	private readonly Dictionary<KernSocket, RouteEntry> _sockets = [];
	private readonly List<KernSse> _sse = [];
	private readonly Dictionary<string, SseHub> _sseHubs = [];
	private bool _started;
	private bool _stopping;

	internal List<RouteEntry> Routes { get; } = [];
	internal override KernApp App => this;

	internal KernApp(int port, string host)
	{
		Port = port;
		Host = host;
	}

	public int Port { get; }
	public string Host { get; }
	/// <summary>true — не печатать строку запуска (полезно в тестах).</summary>
	public bool Quiet { get; set; }
	public int Connections => _sockets.Count + _sse.Count(sse => !sse.Closed);

	// ---------------------------------------------------------------- lifecycle

	internal void Start()
	{
		if (_started)
			return;
		_started = true;
		_listener.Prefixes.Add($"http://{Host}:{Port}/");
		try
		{
			_listener.Start();
		}
		catch (HttpListenerException exception)
		{
			throw new KernPanic(
				$"backend: не удалось запустить http://{Host}:{Port}/: {exception.Message}");
		}

		if (!Quiet)
			Console.Out.WriteLine($"сервер http://{Host}:{Port}/");
		_ = Task.Run(Pump);
	}

	internal void Stop()
	{
		if (_stopping)
			return;
		_stopping = true;
		_queue.Writer.TryComplete();
	}

	private void Cleanup()
	{
		foreach (KernSocket socket in _sockets.Keys.ToArray())
			socket.Close();
		foreach (KernSse sse in _sse.ToArray())
			sse.Close();
		_sockets.Clear();
		_sse.Clear();
		try
		{
			_listener.Stop();
		}
		catch (ObjectDisposedException)
		{
			// Сервер уже остановлен.
		}
	}

	/// <summary>Блокирующий цикл сервера: принимать работу и обрабатывать её в этой VM.</summary>
	internal void Run(Interpreter interpreter)
	{
		Start();
		try
		{
			while (!_stopping)
			{
				KernWork work;
				try
				{
					work = _queue.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
				}
				catch (ChannelClosedException)
				{
					break;
				}

				HandleWork(work, interpreter);
			}
		}
		finally
		{
			Cleanup();
		}
	}

	internal KernWork? AcceptBlocking()
	{
		Start();
		try
		{
			return _queue.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
		}
		catch (ChannelClosedException)
		{
			return null;
		}
	}

	internal void HandleWork(KernWork work, Interpreter interpreter)
	{
		switch (work.Kind)
		{
			case WorkKind.Http:
				HandleHttp(work.Ctx!, interpreter);
				break;
			case WorkKind.WsMessage:
				if (_sockets.TryGetValue(work.Socket!, out RouteEntry? open))
					Guard(() => open.OnMessage?.Invoke(interpreter, work.Socket!, work.Text));
				break;
			case WorkKind.WsClosed:
				if (_sockets.Remove(work.Socket!, out RouteEntry? closed))
					Guard(() => closed.OnClose?.Invoke(interpreter, work.Socket!));
				break;
		}
	}

	private async Task Pump()
	{
		while (!_stopping)
		{
			if (!_listener.IsListening)
			{
				try
				{
					_listener.Start();
				}
				catch (Exception exception) when (
					exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
				{
					break;
				}
			}

			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync().ConfigureAwait(false);
			}
			catch (Exception exception) when (
				exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
			{
				if (_stopping)
					break;
				// Транзиентная ошибка HTTP.sys (клиент оборвал запрос/неверный HTTP):
				// слушатель перезапускается, сервер не завершается.
				await Task.Delay(50).ConfigureAwait(false);
				continue;
			}

			_ = Task.Run(() => Enqueue(context));
		}

		_queue.Writer.TryComplete();
	}

	private async Task Enqueue(HttpListenerContext context)
	{
		try
		{
			string body = "";
			if (!context.Request.IsWebSocketRequest && context.Request.HasEntityBody)
			{
				using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
				body = await reader.ReadToEndAsync().ConfigureAwait(false);
			}

			_queue.Writer.TryWrite(KernWork.Http(new KernCtx(context, body)));
		}
		catch (Exception exception) when (
			exception is HttpListenerException or IOException or ObjectDisposedException)
		{
			try
			{
				context.Response.Abort();
			}
			catch (ObjectDisposedException)
			{
				// Клиент уже отключился.
			}
		}
	}

	// ---------------------------------------------------------------- HTTP

	private void HandleHttp(KernCtx ctx, Interpreter interpreter)
	{
		ctx.Vm = interpreter;
		KernRes res;
		try
		{
			res = Dispatch(ctx);
		}
		catch (Exception exception)
		{
			res = KernRes.Error(500, "ошибка сервера: " + exception.Message);
		}

		if (res.Takeover is { } takeover)
		{
			try
			{
				takeover(interpreter);
				return;
			}
			catch (Exception exception)
			{
				res = KernRes.Error(500, "ошибка сервера: " + exception.Message);
			}
		}

		if (res.Streaming is { } streaming)
		{
			var writer = new KernResWriter(ctx.Raw.Response, res, ctx);
			try
			{
				streaming(writer);
			}
			catch (Exception exception)
			{
				// Заголовки уже отправлены: остаётся закрыть поток, клиент увидит обрыв.
				ctx.Cancelled = true;
				if (!Quiet)
					Console.Error.WriteLine("ошибка потокового ответа: " + exception.Message);
			}
			finally
			{
				writer.Dispose();
			}

			return;
		}

		Send(ctx.Raw.Response, res);
	}

	private KernRes Dispatch(KernCtx ctx)
	{
		bool isWebSocket = ctx.Raw.Request.IsWebSocketRequest;
		RouteEntry? best = null;
		Dictionary<string, string> bestParams = [];
		var pathMatched = false;
		foreach (RouteEntry route in Routes)
		{
			if (!route.Pattern.Match(ctx.Path, out Dictionary<string, string> values))
				continue;
			pathMatched = true;
			bool matches = route.Kind switch
			{
				RouteKind.Http => route.Method == ctx.Method ||
					(ctx.Method == "HEAD" && route.Method == "GET"),
				RouteKind.WebSocket => isWebSocket,
				_ => ctx.Method == "GET" && !isWebSocket,
			};
			if (!matches || (best is not null && route.Pattern.Specificity <= best.Pattern.Specificity))
				continue;
			best = route;
			bestParams = values;
		}

		ctx.RouteParams = bestParams;
		if (best is null)
		{
			KernRes miss = pathMatched && ctx.Method != "OPTIONS"
				? KernRes.Error(405, "метод не поддерживается")
				: KernRes.Error(404, "не найдено");
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
			foreach ((string key, string value) in res.Headers)
				response.Headers[key] = value;
			foreach (string cookie in res.Cookies)
				response.Headers.Add("Set-Cookie", cookie);
			response.ContentLength64 = res.Bytes.Length;
			response.OutputStream.Write(res.Bytes, 0, res.Bytes.Length);
			response.Close();
		}
		catch (Exception exception) when (
			exception is HttpListenerException or ObjectDisposedException or IOException)
		{
			// Клиент отключился до отправки ответа.
		}
	}

	// ---------------------------------------------------------------- WebSocket / SSE

	internal void OpenSocket(KernCtx ctx, RouteEntry route, Interpreter interpreter)
	{
		if (!ctx.Raw.Request.IsWebSocketRequest)
		{
			Send(ctx.Raw.Response, KernRes.Error(400, "ожидался WebSocket"));
			return;
		}

		HttpListenerWebSocketContext webSocket = ctx.Raw.AcceptWebSocketAsync(null).GetAwaiter().GetResult();
		var socket = new KernSocket(webSocket.WebSocket, ctx.Raw.Request, route.Template);
		_sockets[socket] = route;
		Guard(() => route.OnOpen?.Invoke(interpreter, socket));
		_ = socket.ReadLoop(work => _queue.Writer.TryWrite(work));
	}

	internal void OpenSse(KernCtx ctx, string template, KAction<KernSse> onOpen, Interpreter interpreter)
	{
		SseHub hub = Hub(template);
		string? lastHeader = ctx.Header("Last-Event-ID") ?? ctx.Query("last_event_id");
		long parsedLast = 0;
		bool hasLast = !string.IsNullOrWhiteSpace(lastHeader) && long.TryParse(lastHeader, out parsedLast);
		long lastEventId = hasLast ? parsedLast : hub.LastId;
		var sse = new KernSse(ctx.Raw.Response, template, hub, lastEventId);
		sse.Ping();
		if (hasLast)
		{
			foreach (SseEvent missed in hub.After(lastEventId))
				sse.WriteEvent(missed.Id, missed.Event, missed.Data);
		}

		_sse.Add(sse);
		Guard(() => onOpen.Invoke(interpreter, sse));
	}

	private SseHub Hub(string route)
	{
		if (!_sseHubs.TryGetValue(route, out SseHub? hub))
			_sseHubs[route] = hub = new SseHub();
		return hub;
	}

	internal int BroadcastSse(string route, string eventName, string data)
	{
		SseHub hub = Hub(route);
		long id = hub.Next(eventName, data);
		_sse.RemoveAll(sse => sse.Closed);
		return _sse.Where(sse => sse.Route == route).Count(sse => sse.WriteEvent(id, eventName, data));
	}

	internal int BroadcastWs(string route, string text)
	{
		foreach (KernSocket dead in _sockets.Keys.Where(socket => !socket.Open).ToArray())
			_sockets.Remove(dead);
		return _sockets
			.Where(pair => pair.Value.Template == route)
			.Count(pair => pair.Key.Send(text));
	}

	private void Guard(Action action)
	{
		try
		{
			action();
		}
		catch (Exception exception)
		{
			if (!Quiet)
				Console.Error.WriteLine("ошибка в соединении: " + exception.Message);
		}
	}

	internal static KernApp Unwrap(KNativeBox self) => self.Value switch
	{
		KernApp app => app,
		_ => throw new KernPanic($"app: ожидался сервер, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Члены типа <c>app</c> (маршруты наследуются от <c>route_group</c>).</summary>
[KernType("app", Parent = "route_group")]
public static class AppApi
{
	[KernCtor]
	[KernName("app")]
	public static KernApp Create() => new(8080, "localhost");

	[KernCtor]
	[KernName("app")]
	public static KernApp Create(int port) => new(port, "localhost");

	[KernCtor]
	[KernName("app")]
	public static KernApp Create(int port, string host) => new(port, host);

	[KernProperty]
	public static int Port(KNativeBox self) => KernApp.Unwrap(self).Port;

	[KernProperty]
	public static string Host(KNativeBox self) => KernApp.Unwrap(self).Host;

	[KernProperty]
	public static bool Quiet(KNativeBox self) => KernApp.Unwrap(self).Quiet;

	[KernProperty]
	public static void Quiet(KNativeBox self, bool value) => KernApp.Unwrap(self).Quiet = value;

	[KernProperty]
	public static int Connections(KNativeBox self) => KernApp.Unwrap(self).Connections;

	[KernMember]
	public static void Use(KNativeBox self, KFunc<KernCtx, KernChain, KernRes> middleware) =>
		GroupOperations.Use(KernApp.Unwrap(self), middleware);

	[KernMember]
	public static void Use(KNativeBox self, KernMiddleware middleware) =>
		GroupOperations.Use(KernApp.Unwrap(self), middleware);

	[KernMember]
	public static void Get(KNativeBox self, string pattern, KFunc<KernCtx, KernRes> handler) =>
		GroupOperations.Get(KernApp.Unwrap(self), pattern, handler);

	[KernMember]
	public static void Post(KNativeBox self, string pattern, KFunc<KernCtx, KernRes> handler) =>
		GroupOperations.Post(KernApp.Unwrap(self), pattern, handler);

	[KernMember]
	public static void Put(KNativeBox self, string pattern, KFunc<KernCtx, KernRes> handler) =>
		GroupOperations.Put(KernApp.Unwrap(self), pattern, handler);

	[KernMember]
	public static void Patch(KNativeBox self, string pattern, KFunc<KernCtx, KernRes> handler) =>
		GroupOperations.Patch(KernApp.Unwrap(self), pattern, handler);

	[KernMember]
	public static void Delete(KNativeBox self, string pattern, KFunc<KernCtx, KernRes> handler) =>
		GroupOperations.Delete(KernApp.Unwrap(self), pattern, handler);

	[KernMember]
	public static void Route(KNativeBox self, string method, string pattern, KFunc<KernCtx, KernRes> handler) =>
		GroupOperations.Route(KernApp.Unwrap(self), method, pattern, handler);

	[KernMember]
	public static KernGroup Group(KNativeBox self, string prefix) =>
		GroupOperations.Group(KernApp.Unwrap(self), prefix);

	[KernMember]
	[KernName("static_files")]
	public static void StaticFiles(KNativeBox self, string urlPrefix, string directory) =>
		GroupOperations.StaticFiles(KernApp.Unwrap(self), urlPrefix, directory);

	[KernMember]
	public static void Ws(KNativeBox self, string pattern, KAction<KernSocket, string> onMessage) =>
		GroupOperations.Ws(KernApp.Unwrap(self), pattern, onMessage);

	[KernMember]
	public static void Ws(
		KNativeBox self,
		string pattern,
		KAction<KernSocket> onOpen,
		KAction<KernSocket, string> onMessage,
		KAction<KernSocket> onClose) =>
		GroupOperations.Ws(KernApp.Unwrap(self), pattern, onOpen, onMessage, onClose);

	[KernMember]
	public static void Sse(KNativeBox self, string pattern, KAction<KernSse> onOpen) =>
		GroupOperations.Sse(KernApp.Unwrap(self), pattern, onOpen);

	[KernMember]
	public static void Services(KNativeBox self, KValue container)
	{
		if (!KernTypeHelpers.TryUnbox(container, out KernContainer typed))
			throw new KernPanic("services: ожидался контейнер зависимостей (kern.di)");
		KernApp.Unwrap(self).SetServices(typed);
	}

	[KernMember]
	public static void Controller<T>(KNativeBox self, Interpreter interpreter, [KernTypeArgs] KType[] types) =>
		KernApp.Unwrap(self).RegisterController(interpreter, types[^1]);

	[KernMember]
	[KernName("scan_controllers")]
	public static int ScanControllers(KNativeBox self, Interpreter interpreter) =>
		KernApp.Unwrap(self).ScanControllers(interpreter);

	[KernMember]
	public static void Stop(KNativeBox self) => KernApp.Unwrap(self).Stop();

	[KernMember]
	public static Task Serve(KNativeBox self, Interpreter interpreter)
	{
		KernApp.Unwrap(self).Run(interpreter);
		return Task.CompletedTask;
	}

	[KernMember]
	[KernReturn("task<work?>")]
	public static Task<KernWork?> Accept(KNativeBox self) =>
		Task.FromResult(KernApp.Unwrap(self).AcceptBlocking());

	[KernMember]
	public static void Handle(KNativeBox self, KernWork work, Interpreter interpreter) =>
		KernApp.Unwrap(self).HandleWork(work, interpreter);

	[KernMember]
	[KernName("sse_broadcast")]
	public static int SseBroadcast(KNativeBox self, string route, string eventName, string data) =>
		KernApp.Unwrap(self).BroadcastSse(route, eventName, data);

	[KernMember]
	[KernName("ws_broadcast")]
	public static int WsBroadcast(KNativeBox self, string route, string text) =>
		KernApp.Unwrap(self).BroadcastWs(route, text);
}
