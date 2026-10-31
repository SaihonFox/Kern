using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Lib.Backend.Http;
using Kern.Lib.Backend.Middleware;
using Kern.Lib.Backend.Realtime;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Routing;

/// <summary>
/// Группа маршрутов с общим префиксом и своими middleware; приложение `app` — корневая группа.
/// <code>
/// var api = a.group("/api")
/// api.use(middleware.jwt_auth("secret"))
/// api.get("/me") fun (ctx c) -> res res.json(c.user)
/// </code>
/// </summary>
[KernType("route_group")]
public class KernGroup
{
	private readonly KernGroup? _parent;
	private readonly string _prefix;
	internal List<Layer> Layers { get; } = [];

	protected KernGroup() { _prefix = ""; }

	internal KernGroup(KernGroup parent, string prefix)
	{
		_parent = parent;
		_prefix = prefix;
	}

	internal virtual KernApp App => _parent!.App;

	internal string FullPrefix => _parent is null ? _prefix : RoutePattern.Join(_parent.FullPrefix, _prefix);

	/// <summary>Слои от корня к листу: сначала middleware приложения, затем группы.</summary>
	internal List<Layer> CollectLayers()
	{
		var chain = new List<KernGroup>();
		for (var g = this; g is not null; g = g._parent) chain.Add(g);
		chain.Reverse();
		return [.. chain.SelectMany(g => g.Layers)];
	}

	// ------------------------------------------------------------- middleware

	/// <summary>Свой middleware: `g.use(fun(ctx c, chain n) -> res { … n.next() })`. Порядок вызовов — порядок регистрации.</summary>
	[KernMember]
	public void Use(KFunc<KernCtx, KernChain, KernRes> middleware) =>
		Layers.Add((ctx, next) => AsRes(middleware.Invoke(KValue.Obj(ctx), KValue.Obj(next)), "middleware"));

	/// <summary>Готовый middleware из `kern.backend`.</summary>
	[KernMember] public void Use(KernMiddleware middleware) => Layers.Add(middleware.Layer);

	// ------------------------------------------------------------- маршруты

	[KernMember] public void Get(string pattern, KFunc<KernCtx, KernRes> handler) => Add("GET", pattern, handler);
	[KernMember] public void Post(string pattern, KFunc<KernCtx, KernRes> handler) => Add("POST", pattern, handler);
	[KernMember] public void Put(string pattern, KFunc<KernCtx, KernRes> handler) => Add("PUT", pattern, handler);
	[KernMember] public void Patch(string pattern, KFunc<KernCtx, KernRes> handler) => Add("PATCH", pattern, handler);
	[KernMember] public void Delete(string pattern, KFunc<KernCtx, KernRes> handler) => Add("DELETE", pattern, handler);

	/// <summary>Произвольный метод: `g.route("HEAD", "/x", …)`.</summary>
	[KernMember] public void Route(string method, string pattern, KFunc<KernCtx, KernRes> handler) => Add(method.ToUpperInvariant(), pattern, handler);

	/// <summary>Вложенная группа: префикс добавляется к путям, middleware наследуются.</summary>
	[KernMember] public KernGroup Group(string prefix) => new(this, prefix);

	/// <summary>Раздаёт каталог: `a.static_files("/assets", "./public")`; для пустого пути отдаёт index.html. Выход за каталог (`..`) запрещён.</summary>
	[KernMember]
	public void StaticFiles(string urlPrefix, string directory)
	{
		var root = Path.GetFullPath(directory);
		Add("GET", RoutePattern.Join(urlPrefix, "{*path}"), new Terminal(ctx =>
		{
			var rel = ctx.Param("path") ?? "";
			var full = Path.GetFullPath(Path.Combine(root, rel.Length == 0 ? "index.html" : rel));
			if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && full != root) return KernRes.Forbidden();
			if (Directory.Exists(full)) full = Path.Combine(full, "index.html");
			return KernRes.File(full);
		}));
	}

	// ------------------------------------------------------------- WebSocket и SSE

	/// <summary>WebSocket: каждое входящее текстовое сообщение вызывает `onMessage(socket, text)`.</summary>
	[KernMember]
	public void Ws(string pattern, KAction<KernSocket, string> onMessage) => AddWs(pattern, null, onMessage, null);

	/// <summary>WebSocket с событиями открытия и закрытия соединения.</summary>
	[KernMember]
	public void Ws(string pattern, KAction<KernSocket> onOpen, KAction<KernSocket, string> onMessage, KAction<KernSocket> onClose) =>
		AddWs(pattern, onOpen, onMessage, onClose);

	/// <summary>Server-Sent Events: `onOpen(sse)` вызывается при подключении клиента; затем пишите `s.send(...)` когда угодно (в т.ч. через `a.sse_broadcast`).</summary>
	[KernMember]
	public void Sse(string pattern, KAction<KernSse> onOpen)
	{
		var full = RoutePattern.Join(FullPrefix, pattern);
		App.Routes.Add(new RouteEntry(RouteKind.Sse, "GET", full, this, ctx => KernRes.Handover(vm => App.OpenSse(ctx, full, onOpen, vm))) { OnSse = onOpen });
	}

	private void AddWs(string pattern, KAction<KernSocket>? onOpen, KAction<KernSocket, string> onMessage, KAction<KernSocket>? onClose)
	{
		var full = RoutePattern.Join(FullPrefix, pattern);
		RouteEntry? route = null;
		route = new RouteEntry(RouteKind.WebSocket, "GET", full, this, ctx => KernRes.Handover(vm => App.OpenSocket(ctx, route!, vm)))
		{
			OnOpen = onOpen, OnMessage = onMessage, OnClose = onClose,
		};
		App.Routes.Add(route);
	}

	// ------------------------------------------------------------- внутреннее

	private void Add(string method, string pattern, KFunc<KernCtx, KernRes> handler) =>
		Add(method, pattern, new Terminal(ctx => AsRes(handler.Invoke(KValue.Obj(ctx)), "обработчик " + method + " " + pattern)));

	internal RouteEntry Add(string method, string pattern, Terminal terminal)
	{
		var route = new RouteEntry(RouteKind.Http, method, RoutePattern.Join(FullPrefix, pattern), this, terminal);
		App.Routes.Add(route);
		return route;
	}

	internal static KernRes AsRes(KValue v, string what) =>
		v.Ref as KernRes ?? throw new KernPanic($"{what} должен вернуть res, а вернул {(v.IsNilOrNone ? "nil" : v.Kind.ToString())}");
}
