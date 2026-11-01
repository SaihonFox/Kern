using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Lib.Backend.Http;
using Kern.Lib.Backend.Middleware;
using Kern.Lib.Backend.Realtime;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Routing;

/// <summary>
/// Группа маршрутов с общим префиксом и middleware. Корень — <see cref="KernApp"/>.
/// </summary>
[KernType("route_group")]
public class KernGroup
{
	private readonly KernGroup? _parent;
	private readonly string _prefix;

	internal List<Layer> Layers { get; } = [];

	protected KernGroup() => _prefix = "";

	internal KernGroup(KernGroup parent, string prefix)
	{
		_parent = parent;
		_prefix = prefix;
	}

	internal virtual KernApp App => _parent!.App;

	internal string FullPrefix => _parent is null ? _prefix : RoutePattern.Join(_parent.FullPrefix, _prefix);

	/// <summary>Middleware от корня к этой группе; порядок важен.</summary>
	internal List<Layer> CollectLayers()
	{
		var chain = new List<KernGroup>();
		for (KernGroup? group = this; group is not null; group = group._parent)
			chain.Add(group);
		chain.Reverse();
		return [.. chain.SelectMany(group => group.Layers)];
	}

	internal void UseLayer(Layer layer) => Layers.Add(layer);

	internal KernGroup MakeGroup(string prefix) => new(this, prefix);

	internal RouteEntry AddHttp(string method, string pattern, KFunc<KernCtx, KernRes> handler) =>
		AddHttp(method, pattern, ctx => AsRes(
			handler.Invoke(ctx.Vm ?? throw new KernPanic("backend: нет VM в контексте"), ctx),
			"обработчик " + method + " " + pattern));

	internal RouteEntry AddHttp(string method, string pattern, Terminal terminal)
	{
		var route = new RouteEntry(
			RouteKind.Http,
			method,
			RoutePattern.Join(FullPrefix, pattern),
			this,
			terminal);
		App.Routes.Add(route);
		return route;
	}

	internal void AddStaticFiles(string urlPrefix, string directory)
	{
		string root = Path.GetFullPath(directory);
		AddHttp("GET", RoutePattern.Join(urlPrefix, "{*path}"), ctx =>
		{
			string relative = ctx.Param("path") ?? "";
			string full = Path.GetFullPath(Path.Combine(root, relative.Length == 0 ? "index.html" : relative));
			if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && full != root)
				return KernRes.Error(403, "доступ запрещён");
			if (Directory.Exists(full))
				full = Path.Combine(full, "index.html");
			return KernRes.File(full);
		});
	}

	internal RouteEntry AddWs(
		string pattern,
		KAction<KernSocket>? onOpen,
		KAction<KernSocket, string> onMessage,
		KAction<KernSocket>? onClose)
	{
		string full = RoutePattern.Join(FullPrefix, pattern);
		RouteEntry? route = null;
		route = new RouteEntry(
			RouteKind.WebSocket,
			"GET",
			full,
			this,
			ctx => KernRes.Handover(vm => App.OpenSocket(ctx, route!, vm)))
		{
			OnOpen = onOpen,
			OnMessage = onMessage,
			OnClose = onClose,
		};
		App.Routes.Add(route);
		return route;
	}

	internal RouteEntry AddSse(string pattern, KAction<KernSse> onOpen)
	{
		string full = RoutePattern.Join(FullPrefix, pattern);
		var route = new RouteEntry(
			RouteKind.Sse,
			"GET",
			full,
			this,
			ctx => KernRes.Handover(vm => App.OpenSse(ctx, full, onOpen, vm)))
		{
			OnSse = onOpen,
		};
		App.Routes.Add(route);
		return route;
	}

	internal static KernRes AsRes(KValue value, string what)
	{
		if (value.Ref is KNativeBox box && box.Value is KernRes res)
			return res;
		throw new KernPanic(
			$"{what}: ожидался res, получено {(value.IsNilOrNone ? "nil" : value.TypeName)}");
	}

	internal static KernRes AsRes(KernRes res, string what)
	{
		_ = what;
		return res;
	}

	internal static KernGroup Require(KNativeBox self) => self.Value switch
	{
		KernGroup group => group,
		_ => throw new KernPanic($"route_group: ожидалась группа, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Общие члены <c>app</c> и <c>route_group</c>.</summary>
internal static class GroupOperations
{
	public static void Use(KernGroup group, KFunc<KernCtx, KernChain, KernRes> middleware) =>
		group.UseLayer((ctx, next) => KernGroup.AsRes(
			middleware.Invoke(
				ctx.Vm ?? throw new KernPanic("backend: нет VM в контексте"),
				ctx,
				next),
			"middleware"));

	public static void Use(KernGroup group, KernMiddleware middleware) => group.UseLayer(middleware.Layer);

	public static void Get(KernGroup group, string pattern, KFunc<KernCtx, KernRes> handler) =>
		group.AddHttp("GET", pattern, handler);

	public static void Post(KernGroup group, string pattern, KFunc<KernCtx, KernRes> handler) =>
		group.AddHttp("POST", pattern, handler);

	public static void Put(KernGroup group, string pattern, KFunc<KernCtx, KernRes> handler) =>
		group.AddHttp("PUT", pattern, handler);

	public static void Patch(KernGroup group, string pattern, KFunc<KernCtx, KernRes> handler) =>
		group.AddHttp("PATCH", pattern, handler);

	public static void Delete(KernGroup group, string pattern, KFunc<KernCtx, KernRes> handler) =>
		group.AddHttp("DELETE", pattern, handler);

	public static void Route(KernGroup group, string method, string pattern, KFunc<KernCtx, KernRes> handler) =>
		group.AddHttp(method.ToUpperInvariant(), pattern, handler);

	public static KernGroup Group(KernGroup group, string prefix) => group.MakeGroup(prefix);

	public static void StaticFiles(KernGroup group, string urlPrefix, string directory) =>
		group.AddStaticFiles(urlPrefix, directory);

	public static void Ws(KernGroup group, string pattern, KAction<KernSocket, string> onMessage) =>
		group.AddWs(pattern, null, onMessage, null);

	public static void Ws(
		KernGroup group,
		string pattern,
		KAction<KernSocket> onOpen,
		KAction<KernSocket, string> onMessage,
		KAction<KernSocket> onClose) =>
		group.AddWs(pattern, onOpen, onMessage, onClose);

	public static void Sse(KernGroup group, string pattern, KAction<KernSse> onOpen) =>
		group.AddSse(pattern, onOpen);
}

[KernType("route_group")]
public static class GroupApi
{
	[KernMember]
	public static void Use(KNativeBox self, KFunc<KernCtx, KernChain, KernRes> middleware) =>
		GroupOperations.Use(KernGroup.Require(self), middleware);

	[KernMember]
	public static void Use(KNativeBox self, KernMiddleware middleware) =>
		GroupOperations.Use(KernGroup.Require(self), middleware);

	[KernMember]
	public static void Get(KNativeBox self, string pattern, KFunc<KernCtx, KernRes> handler) =>
		GroupOperations.Get(KernGroup.Require(self), pattern, handler);

	[KernMember]
	public static void Post(KNativeBox self, string pattern, KFunc<KernCtx, KernRes> handler) =>
		GroupOperations.Post(KernGroup.Require(self), pattern, handler);

	[KernMember]
	public static void Put(KNativeBox self, string pattern, KFunc<KernCtx, KernRes> handler) =>
		GroupOperations.Put(KernGroup.Require(self), pattern, handler);

	[KernMember]
	public static void Patch(KNativeBox self, string pattern, KFunc<KernCtx, KernRes> handler) =>
		GroupOperations.Patch(KernGroup.Require(self), pattern, handler);

	[KernMember]
	public static void Delete(KNativeBox self, string pattern, KFunc<KernCtx, KernRes> handler) =>
		GroupOperations.Delete(KernGroup.Require(self), pattern, handler);

	[KernMember]
	public static void Route(KNativeBox self, string method, string pattern, KFunc<KernCtx, KernRes> handler) =>
		GroupOperations.Route(KernGroup.Require(self), method, pattern, handler);

	[KernMember]
	public static KernGroup Group(KNativeBox self, string prefix) =>
		GroupOperations.Group(KernGroup.Require(self), prefix);

	[KernMember]
	[KernName("static_files")]
	public static void StaticFiles(KNativeBox self, string urlPrefix, string directory) =>
		GroupOperations.StaticFiles(KernGroup.Require(self), urlPrefix, directory);

	[KernMember]
	public static void Ws(KNativeBox self, string pattern, KAction<KernSocket, string> onMessage) =>
		GroupOperations.Ws(KernGroup.Require(self), pattern, onMessage);

	[KernMember]
	public static void Ws(
		KNativeBox self,
		string pattern,
		KAction<KernSocket> onOpen,
		KAction<KernSocket, string> onMessage,
		KAction<KernSocket> onClose) =>
		GroupOperations.Ws(KernGroup.Require(self), pattern, onOpen, onMessage, onClose);

	[KernMember]
	public static void Sse(KNativeBox self, string pattern, KAction<KernSse> onOpen) =>
		GroupOperations.Sse(KernGroup.Require(self), pattern, onOpen);
}
