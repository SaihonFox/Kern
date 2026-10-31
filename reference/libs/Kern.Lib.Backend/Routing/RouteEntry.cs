using Kern.Lib.Backend.Http;
using Kern.Lib.Backend.Middleware;
using Kern.Binding.Wrappers;

namespace Kern.Lib.Backend.Routing;

internal enum RouteKind { Http, WebSocket, Sse }

/// <summary>Зарегистрированный маршрут: шаблон, обработчик и группа (от неё берётся цепочка middleware).</summary>
internal sealed class RouteEntry(RouteKind kind, string method, string template, KernGroup group, Terminal terminal)
{
	public RouteKind Kind { get; } = kind;
	public string Method { get; } = method;
	public string Template { get; } = template;
	public RoutePattern Pattern { get; } = new(template);
	public KernGroup Group { get; } = group;
	public Terminal Terminal { get; } = terminal;
	/// <summary>Дополнительные слои конкретного маршрута (атрибуты #[auth], #[roles]).</summary>
	public List<Layer> Extra { get; } = [];

	public KAction<Realtime.KernSocket>? OnOpen { get; init; }
	public KAction<Realtime.KernSocket, string>? OnMessage { get; init; }
	public KAction<Realtime.KernSocket>? OnClose { get; init; }
	public KAction<Realtime.KernSse>? OnSse { get; init; }
}
