using System.Net;
using System.Text;
using Kern.Binding.Attributes;

namespace Kern.Lib.Backend.Realtime;

/// <summary>Server-Sent Events: `s.send("tick", "1")` пишет событие в открытое соединение; `s.close()` завершает поток.</summary>
[KernType("sse")]
public sealed class KernSse
{
	private readonly HttpListenerResponse _response;
	internal string Route { get; }
	internal bool Closed { get; private set; }

	internal KernSse(HttpListenerResponse response, string route)
	{
		_response = response;
		Route = route;
		Id = Guid.NewGuid().ToString("N");
		response.StatusCode = 200;
		response.ContentType = "text/event-stream; charset=utf-8";
		response.Headers["Cache-Control"] = "no-cache";
		response.SendChunked = true;
	}

	[KernProperty] public string Id { get; }
	[KernProperty] public bool Open => !Closed;

	/// <summary>Событие с именем (`event:`) и данными; многострочные данные разбиваются на строки `data:`.</summary>
	[KernMember]
	public bool Send(string eventName, string data)
	{
		var sb = new StringBuilder();
		if (eventName.Length > 0) sb.Append("event: ").Append(eventName).Append('\n');
		foreach (var line in data.Replace("\r\n", "\n").Split('\n')) sb.Append("data: ").Append(line).Append('\n');
		sb.Append('\n');
		return Write(sb.ToString());
	}

	/// <summary>Событие без имени.</summary>
	internal bool Ping() => Write(": connected\n\n");

	[KernMember] public bool SendData(string data) => Send("", data);

	[KernMember]
	public void Close()
	{
		if (Closed) return;
		Closed = true;
		try { _response.Close(); }
		catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
	}

	private bool Write(string text)
	{
		if (Closed) return false;
		try
		{
			var bytes = Encoding.UTF8.GetBytes(text);
			_response.OutputStream.Write(bytes);
			_response.OutputStream.Flush();
			return true;
		}
		catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException)
		{
			Closed = true;
			return false;
		}
	}
}
