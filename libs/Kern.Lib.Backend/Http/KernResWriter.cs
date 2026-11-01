using System.Net;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Http;

/// <summary>
/// Потоковая запись HTTP-ответа: <c>res.stream("text/plain", fun (res_writer w) { w.write("...") })</c>.
/// Ответ отправляется chunked; клиент может оборвать соединение — тогда <see cref="Cancelled"/>.
/// </summary>
[KernType("res_writer")]
public sealed class KernResWriter : IDisposable
{
	private readonly HttpListenerResponse _response;
	private readonly KernCtx _ctx;
	private bool _closed;

	internal KernResWriter(HttpListenerResponse response, KernRes res, KernCtx ctx)
	{
		_response = response;
		_ctx = ctx;
		response.StatusCode = res.Status;
		response.ContentType = res.ContentType;
		foreach ((string key, string value) in res.Headers)
			response.Headers[key] = value;
		foreach (string cookie in res.Cookies)
			response.Headers.Add("Set-Cookie", cookie);
		response.SendChunked = true;
	}

	public bool Cancelled => _ctx.Cancelled;

	public bool Closed => _closed;

	public bool Write(string text)
	{
		if (_closed || _ctx.Cancelled)
			return false;
		try
		{
			byte[] bytes = Encoding.UTF8.GetBytes(text);
			_response.OutputStream.Write(bytes, 0, bytes.Length);
			_response.OutputStream.Flush();
			return true;
		}
		catch (Exception exception) when (
			exception is HttpListenerException or ObjectDisposedException or IOException)
		{
			_ctx.Cancelled = true;
			_closed = true;
			return false;
		}
	}

	public bool WriteLine(string text) => Write(text + "\n");

	public bool Flush()
	{
		if (_closed || _ctx.Cancelled)
			return false;
		try
		{
			_response.OutputStream.Flush();
			return true;
		}
		catch (Exception exception) when (
			exception is HttpListenerException or ObjectDisposedException or IOException)
		{
			_ctx.Cancelled = true;
			_closed = true;
			return false;
		}
	}

	public void Close() => Dispose();

	public void Dispose()
	{
		if (_closed)
			return;
		_closed = true;
		try
		{
			_response.Close();
		}
		catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or IOException)
		{
			// Клиент уже отключился.
		}
	}

	internal static KernResWriter Require(KNativeBox self) => self.Value switch
	{
		KernResWriter writer => writer,
		_ => throw new KernPanic($"res_writer: ожидался поток ответа, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Члены типа <c>res_writer</c>.</summary>
[KernType("res_writer")]
public static class ResWriterApi
{
	[KernProperty]
	public static bool Cancelled(KNativeBox self) => KernResWriter.Require(self).Cancelled;

	[KernProperty]
	public static bool Closed(KNativeBox self) => KernResWriter.Require(self).Closed;

	[KernMember]
	public static bool Write(KNativeBox self, string text) => KernResWriter.Require(self).Write(text);

	[KernMember]
	[KernName("write_line")]
	public static bool WriteLine(KNativeBox self, string text) => KernResWriter.Require(self).WriteLine(text);

	[KernMember]
	public static bool Flush(KNativeBox self) => KernResWriter.Require(self).Flush();

	[KernMember]
	public static void Close(KNativeBox self) => KernResWriter.Require(self).Close();
}
