using System.Net;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Realtime;

/// <summary>Server-Sent Events: <c>s.send("tick", "1")</c>; <c>s.close()</c> закрывает поток.</summary>
[KernType("sse")]
public sealed class KernSse
{
	private readonly HttpListenerResponse _response;
	private readonly SseHub _hub;

	internal string Route { get; }
	internal bool Closed { get; private set; }
	public string Id { get; }
	public bool Open => !Closed;
	public long LastEventId { get; }

	internal KernSse(HttpListenerResponse response, string route, SseHub hub, long lastEventId)
	{
		_response = response;
		_hub = hub;
		Route = route;
		LastEventId = lastEventId;
		Id = Guid.NewGuid().ToString("N");
		response.StatusCode = 200;
		response.ContentType = "text/event-stream; charset=utf-8";
		response.Headers["Cache-Control"] = "no-cache";
		response.SendChunked = true;
	}

	public bool Send(string eventName, string data)
	{
		long id = _hub.Next(eventName, data);
		return WriteEvent(id, eventName, data);
	}

	public bool SendData(string data) => Send("", data);

	internal bool WriteEvent(long id, string eventName, string data)
	{
		var builder = new StringBuilder();
		builder.Append("id: ").Append(id).Append('\n');
		if (eventName.Length > 0)
			builder.Append("event: ").Append(eventName).Append('\n');
		foreach (string line in data.Replace("\r\n", "\n").Split('\n'))
			builder.Append("data: ").Append(line).Append('\n');
		builder.Append('\n');
		return Write(builder.ToString());
	}

	internal bool Ping() => Write(": connected\n\n");

	public void Close()
	{
		if (Closed)
			return;
		Closed = true;
		try
		{
			_response.Close();
		}
		catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
		{
			// Уже закрыт.
		}
	}

	private bool Write(string text)
	{
		if (Closed)
			return false;
		try
		{
			byte[] bytes = Encoding.UTF8.GetBytes(text);
			_response.OutputStream.Write(bytes);
			_response.OutputStream.Flush();
			return true;
		}
		catch (Exception exception) when (
			exception is HttpListenerException or ObjectDisposedException or IOException)
		{
			Closed = true;
			return false;
		}
	}

	internal static KernSse Require(KNativeBox self) => self.Value switch
	{
		KernSse sse => sse,
		_ => throw new KernPanic($"sse: ожидался SSE-поток, получено {self.Value.GetType().Name}"),
	};
}

[KernType("sse")]
public static class SseApi
{
	[KernProperty]
	public static string Id(KNativeBox self) => KernSse.Require(self).Id;

	[KernProperty]
	public static bool Open(KNativeBox self) => KernSse.Require(self).Open;

	[KernProperty]
	[KernName("last_event_id")]
	public static long LastEventId(KNativeBox self) => KernSse.Require(self).LastEventId;

	[KernMember]
	public static bool Send(KNativeBox self, string eventName, string data) =>
		KernSse.Require(self).Send(eventName, data);

	[KernMember]
	[KernName("send_data")]
	public static bool SendData(KNativeBox self, string data) => KernSse.Require(self).SendData(data);

	[KernMember]
	public static void Close(KNativeBox self) => KernSse.Require(self).Close();
}
