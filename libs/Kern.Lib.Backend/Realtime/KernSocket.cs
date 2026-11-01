using System.Net;
using System.Net.WebSockets;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Realtime;

/// <summary>WebSocket-соединение: <c>s.send("текст")</c>, <c>s.query("…")</c>, <c>s.close()</c>.</summary>
[KernType("socket")]
public sealed class KernSocket
{
	private readonly WebSocket _socket;
	private readonly HttpListenerRequest _request;

	internal string Route { get; }
	internal bool IsOpen => _socket.State == WebSocketState.Open;
	public string Id { get; }
	public string Path => _request.Url?.AbsolutePath ?? "/";
	public bool Open => IsOpen;

	internal KernSocket(WebSocket socket, HttpListenerRequest request, string route)
	{
		_socket = socket;
		_request = request;
		Route = route;
		Id = Guid.NewGuid().ToString("N");
	}

	public string? Query(string name) => _request.QueryString[name];

	public string? Header(string name) => _request.Headers[name];

	public bool Send(string text)
	{
		if (!IsOpen)
			return false;
		try
		{
			_socket.SendAsync(
					Encoding.UTF8.GetBytes(text),
					WebSocketMessageType.Text,
					true,
					CancellationToken.None)
				.GetAwaiter()
				.GetResult();
			return true;
		}
		catch (Exception exception) when (
			exception is WebSocketException or ObjectDisposedException or InvalidOperationException)
		{
			return false;
		}
	}

	public void Close()
	{
		if (!IsOpen)
			return;
		try
		{
			_socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
				.GetAwaiter()
				.GetResult();
		}
		catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException)
		{
			// Соединение уже закрыто.
		}
	}

	/// <summary>Читает сообщения и складывает их в очередь работы VM.</summary>
	internal async Task ReadLoop(Action<KernWork> post)
	{
		var buffer = new byte[16 * 1024];
		try
		{
			while (_socket.State == WebSocketState.Open)
			{
				using var stream = new MemoryStream();
				WebSocketReceiveResult result;
				do
				{
					result = await _socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
					if (result.MessageType == WebSocketMessageType.Close)
					{
						try
						{
							await _socket.CloseOutputAsync(
									WebSocketCloseStatus.NormalClosure,
									"",
									CancellationToken.None)
								.ConfigureAwait(false);
						}
						catch (Exception exception) when (
							exception is WebSocketException or ObjectDisposedException)
						{
							// Клиент уже закрыл соединение.
						}

						goto closed;
					}

					stream.Write(buffer, 0, result.Count);
				}
				while (!result.EndOfMessage);

				if (result.MessageType == WebSocketMessageType.Text)
					post(KernWork.Message(this, Encoding.UTF8.GetString(stream.ToArray())));
			}
		}
		catch (Exception exception) when (
			exception is WebSocketException or ObjectDisposedException or OperationCanceledException)
		{
			// Соединение оборвалось — сообщим о закрытии.
		}

	closed:
		post(KernWork.Closed(this));
	}

	internal static KernSocket Require(KNativeBox self) => self.Value switch
	{
		KernSocket socket => socket,
		_ => throw new KernPanic($"socket: ожидалось соединение, получено {self.Value.GetType().Name}"),
	};
}

[KernType("socket")]
public static class SocketApi
{
	[KernProperty]
	public static string Id(KNativeBox self) => KernSocket.Require(self).Id;

	[KernProperty]
	public static string Path(KNativeBox self) => KernSocket.Require(self).Path;

	[KernProperty]
	public static bool Open(KNativeBox self) => KernSocket.Require(self).Open;

	[KernMember]
	public static string? Query(KNativeBox self, string name) => KernSocket.Require(self).Query(name);

	[KernMember]
	public static string? Header(KNativeBox self, string name) => KernSocket.Require(self).Header(name);

	[KernMember]
	public static bool Send(KNativeBox self, string text) => KernSocket.Require(self).Send(text);

	[KernMember]
	public static void Close(KNativeBox self) => KernSocket.Require(self).Close();
}
