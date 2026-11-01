using Kern.Binding.Attributes;
using Kern.Lib.Backend.Http;

namespace Kern.Lib.Backend.Realtime;

internal enum WorkKind
{
	Http,
	WsMessage,
	WsClosed,
}

/// <summary>Единица работы сервера: HTTP-запрос или событие WebSocket.</summary>
[KernType("work")]
public sealed class KernWork
{
	internal WorkKind Kind { get; private init; }
	internal KernCtx? Ctx { get; private init; }
	internal KernSocket? Socket { get; private init; }
	internal string Text { get; private init; } = "";

	internal static KernWork Http(KernCtx ctx) => new() { Kind = WorkKind.Http, Ctx = ctx };

	internal static KernWork Message(KernSocket socket, string text) =>
		new() { Kind = WorkKind.WsMessage, Socket = socket, Text = text };

	internal static KernWork Closed(KernSocket socket) =>
		new() { Kind = WorkKind.WsClosed, Socket = socket };
}
