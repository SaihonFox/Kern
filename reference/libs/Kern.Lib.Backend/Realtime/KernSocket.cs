using System.Net;
using System.Net.WebSockets;
using System.Text;
using Kern.Binding.Attributes;

namespace Kern.Lib.Backend.Realtime;

/// <summary>WebSocket-соединение. Сообщения приходят в обработчик `a.ws(...)`, отправка — `s.send("текст")`.</summary>
[KernType("socket")]
public sealed class KernSocket
{
	private readonly WebSocket _socket;
	private readonly HttpListenerRequest _request;
	internal string Route { get; }
	internal bool IsOpen => _socket.State == WebSocketState.Open;

	internal KernSocket(WebSocket socket, HttpListenerRequest request, string route)
	{
		_socket = socket;
		_request = request;
		Route = route;
		Id = Guid.NewGuid().ToString("N");
	}

	[KernProperty] public string Id { get; }
	[KernProperty] public string Path => _request.Url?.AbsolutePath ?? "/";
	[KernProperty] public bool Open => IsOpen;

	[KernMember] public string? Query(string name) => _request.QueryString[name];
	[KernMember] public string? Header(string name) => _request.Headers[name];

	/// <summary>Отправляет текстовое сообщение; false — соединение уже закрыто.</summary>
	[KernMember]
	public bool Send(string text)
	{
		if (!IsOpen) return false;
		try
		{
			_socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
			return true;
		}
		catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException) { return false; }
	}

	[KernMember]
	public void Close()
	{
		if (!IsOpen) return;
		try { _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).GetAwaiter().GetResult(); }
		catch (Exception e) when (e is WebSocketException or ObjectDisposedException) { }
	}

	/// <summary>Фоновое чтение: каждое сообщение и закрытие кладётся в очередь сервера, Kern-код вызывается позже в потоке VM.</summary>
	internal async Task ReadLoop(Action<KernWork> post)
	{
		var buffer = new byte[16 * 1024];
		try
		{
			while (_socket.State == WebSocketState.Open)
			{
				using var ms = new MemoryStream();
				WebSocketReceiveResult r;
				do
				{
					r = await _socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
					if (r.MessageType == WebSocketMessageType.Close) goto closed;
					ms.Write(buffer, 0, r.Count);
				} while (!r.EndOfMessage);
				if (r.MessageType == WebSocketMessageType.Text) post(KernWork.Message(this, Encoding.UTF8.GetString(ms.ToArray())));
			}
		}
		catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException) { }
	closed:
		post(KernWork.Closed(this));
	}
}
