using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Runtime.Json;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Net;

/// <summary>Ответ HTTP-клиента.</summary>
[KernType("http_reply")]
public sealed class HttpReply(int status, string body, Dictionary<string, string> headers)
{
	[KernProperty] public int Status { get; } = status;
	[KernProperty] public bool IsOk => Status is >= 200 and < 300;
	[KernProperty] public string Body { get; } = body;
	[KernProperty] public KDict<string, string> Headers
	{
		get
		{
			var d = new KDictObject(TypeFactory.Str, TypeFactory.Str);
			foreach (var (k, v) in headers) d.Items[KValue.Str(k)] = KValue.Str(v);
			return new KDict<string, string>(d);
		}
	}

	[KernMember] public string? Header(string name) => headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;

	/// <summary>Тело как JSON (динамические значения).</summary>
	[KernMember] public KResult<object, string> Json() => JsonApi.Parse(Body);
}

/// <summary>
/// HTTP-клиент: <c>match await http.get("https://…") { Ok(r) -> r.body; Err(e) -> … }</c>.
/// Сетевые ошибки возвращаются как Err, а не как исключения. Таймаут по умолчанию — 30 с.
/// </summary>
[KernType("http")]
public static class HttpApi
{
	private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

	private static async Task<KResult<HttpReply, string>> SendAsync(HttpMethod method, string url, string? body, string contentType, KDict<string, string>? headers)
	{
		try
		{
			using var req = new HttpRequestMessage(method, url);
			if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, contentType);
			if (headers is { } h)
				foreach (var (k, v) in h.Items) req.Headers.TryAddWithoutValidation(k.AsStr, v.AsStr);
			using var res = await Client.SendAsync(req).ConfigureAwait(false);
			var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
			var map = new Dictionary<string, string>();
			foreach (var hd in res.Headers.Concat(res.Content.Headers)) map[hd.Key.ToLowerInvariant()] = string.Join(", ", hd.Value);
			return KResult<HttpReply, string>.Ok(KValue.Obj(new HttpReply((int)res.StatusCode, text, map)));
		}
		catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
		{
			return Kv.Err<HttpReply>(e.Message);
		}
	}

	[KernStatic] public static Task<KResult<HttpReply, string>> Get(string url) => SendAsync(HttpMethod.Get, url, null, "", null);
	[KernStatic] public static Task<KResult<HttpReply, string>> Get(string url, KDict<string, string> headers) => SendAsync(HttpMethod.Get, url, null, "", headers);
	[KernStatic] public static Task<KResult<HttpReply, string>> Delete(string url) => SendAsync(HttpMethod.Delete, url, null, "", null);

	[KernStatic] public static Task<KResult<HttpReply, string>> Post(string url, string body) => SendAsync(HttpMethod.Post, url, body, "text/plain", null);
	[KernStatic] public static Task<KResult<HttpReply, string>> Post(string url, string body, string contentType) => SendAsync(HttpMethod.Post, url, body, contentType, null);
	[KernStatic] public static Task<KResult<HttpReply, string>> PostJson(string url, KValue value, [KernVm] Kern.Vm.Execution.Interpreter vm) =>
		SendAsync(HttpMethod.Post, url, JsonApi.ToStr(vm, value), "application/json", null);

	[KernStatic] public static Task<KResult<HttpReply, string>> Put(string url, string body, string contentType) => SendAsync(HttpMethod.Put, url, body, contentType, null);

	[KernStatic] public static Task<KResult<HttpReply, string>> Request(string method, string url, string? body, KDict<string, string> headers) =>
		SendAsync(new HttpMethod(method.ToUpperInvariant()), url, body, "text/plain", headers);
}
