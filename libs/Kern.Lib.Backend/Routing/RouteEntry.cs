using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Lib.Backend.Http;
using Kern.Lib.Backend.Middleware;
using Kern.Lib.Backend.Realtime;

namespace Kern.Lib.Backend.Routing;

internal enum RouteKind
{
	Http,
	WebSocket,
	Sse,
}

/// <summary>Маршрут: метод, шаблон, группа (её middleware) и обработчик.</summary>
internal sealed class RouteEntry(
	RouteKind kind,
	string method,
	string template,
	KernGroup group,
	Terminal terminal)
{
	public RouteKind Kind { get; } = kind;
	public string Method { get; } = method;
	public string Template { get; } = template;
	public RoutePattern Pattern { get; } = new(template);
	public KernGroup Group { get; } = group;
	public Terminal Terminal { get; } = terminal;

	/// <summary>Дополнительные слои (например, <c>#[auth]</c>/<c>#[roles]</c> контроллера).</summary>
	public List<Layer> Extra { get; } = [];

	public KAction<KernSocket>? OnOpen { get; init; }
	public KAction<KernSocket, string>? OnMessage { get; init; }
	public KAction<KernSocket>? OnClose { get; init; }
	public KAction<KernSse>? OnSse { get; init; }
}
