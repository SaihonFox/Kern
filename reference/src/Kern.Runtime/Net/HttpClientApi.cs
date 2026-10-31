using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Bytecode;
using Kern.Runtime.Core;
using Kern.Runtime.Json;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Reflection;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Net;

/// <summary>Сырой HTTP-обмен без значений VM: общий для обоих вариантов типизированного клиента.</summary>
internal static class HttpRaw
{
	private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

	/// <summary>Возвращает (текст ответа, null) при 2xx; (null, сообщение) при сетевой ошибке или коде вне 2xx.</summary>
	public static async Task<(string? Body, string? Error)> SendAsync(string method, string url, string? body, string contentType, IReadOnlyDictionary<string, string> headers)
	{
		try
		{
			using var req = new HttpRequestMessage(new HttpMethod(method), url);
			if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, contentType);
			foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
			using var res = await Client.SendAsync(req).ConfigureAwait(false);
			string text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
			return res.IsSuccessStatusCode ? (text, null) : (null, $"HTTP {(int)res.StatusCode}: {text}");
		}
		catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
		{
			return (null, e.Message);
		}
	}

	/// <summary>Разбор JSON в тип T выполняется в потоке планировщика VM (типы VM не потокобезопасны).</summary>
	public static Task<KResult<T, string>> Finish<T>(Interpreter vm, Task<(string? Body, string? Error)> raw, KType type) => Run<T>(vm, raw, type);

	private static async Task<KResult<T, string>> Run<T>(Interpreter vm, Task<(string? Body, string? Error)> raw, KType type)
	{
		var (body, error) = await raw.ConfigureAwait(false);
		if (error is not null) return Kv.Err<T>(error);
		var tcs = new TaskCompletionSource<KResult<T, string>>();
		vm.Scheduler.Post(() =>
		{
			try { tcs.SetResult(KResult<T, string>.Ok(ParseBody(vm, body!, type))); }
			catch (Exception e) when (e is not KernExit) { tcs.SetResult(Kv.Err<T>("разбор ответа: " + e.Message)); }
		});
		return await tcs.Task.ConfigureAwait(false);
	}

	public static KValue ParseBody(Interpreter vm, string body, KType type) =>
		type is KPrimType { Kind: PrimKind.Str } ? KValue.Str(body) : JsonApi.FromJsonText(vm, body, type);

	public static string Join(string baseUrl, string path) =>
		path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
			? path
			: baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
}

/// <summary>
/// Вариант A — типизированный клиент с базовым адресом: тело ответа сразу разбирается в T.
/// <code>
/// http_client api("https://api.example.com")
/// api.bearer("token")
/// match await api.get&lt;User&gt;("/users/1") { Ok(u) -> println(u.name); Err(e) -> println(e) }
/// match await api.post&lt;User&gt;("/users", User(0, "Аня")) { … }   // тело — JSON из значения (str уходит как есть)
/// </code>
/// Код ответа вне 2xx → <c>Err("HTTP 404: …")</c>. <c>T = str</c> возвращает тело без разбора.
/// Вариант B (интерфейс по атрибутам) — <c>http_client.proxy&lt;Api&gt;(base_url)</c>.
/// </summary>
[KernType("http_client")]
public sealed class HttpClientApi
{
	private readonly string _baseUrl;
	private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

	[KernCtor] public HttpClientApi(string baseUrl) => _baseUrl = baseUrl;

	[KernMember] public void Header(string name, string value) => _headers[name] = value;
	[KernMember] public void Bearer(string token) => _headers["Authorization"] = "Bearer " + token;

	[KernMember] public Task<KResult<T, string>> Get<T>(string path, [KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs) => Call<T>("GET", path, default, false, vm, typeArgs);
	[KernMember] public Task<KResult<T, string>> Delete<T>(string path, [KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs) => Call<T>("DELETE", path, default, false, vm, typeArgs);
	[KernMember] public Task<KResult<T, string>> Post<T>(string path, KValue body, [KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs) => Call<T>("POST", path, body, true, vm, typeArgs);
	[KernMember] public Task<KResult<T, string>> Put<T>(string path, KValue body, [KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs) => Call<T>("PUT", path, body, true, vm, typeArgs);
	[KernMember] public Task<KResult<T, string>> Patch<T>(string path, KValue body, [KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs) => Call<T>("PATCH", path, body, true, vm, typeArgs);

	private Task<KResult<T, string>> Call<T>(string method, string path, KValue body, bool hasBody, Interpreter vm, KType[] typeArgs)
	{
		var (text, ctype) = hasBody ? EncodeBody(vm, body) : (null, "");
		var raw = HttpRaw.SendAsync(method, HttpRaw.Join(_baseUrl, path), text, ctype, new Dictionary<string, string>(_headers));
		return HttpRaw.Finish<T>(vm, raw, Kv.Last(typeArgs));
	}

	internal static (string Text, string ContentType) EncodeBody(Interpreter vm, KValue body) =>
		body.Kind == ValueKind.Str ? (body.AsStr, "text/plain") : (JsonApi.ToStr(vm, body), "application/json");

	// ---------- вариант B: прокси по атрибутам ----------

	/// <summary>
	/// Вариант B — Refit-подобный: контракт описывается абстрактным <c>@type</c>, метод помечается атрибутом
	/// <c>#[get("/users/{id}")]</c> (также <c>post/put/patch/delete</c>), возвращает <c>task&lt;result&lt;T, str&gt;&gt;</c>.
	/// <code>
	/// @type UsersApi {
	///     #[get("/users/{id}")]  @fun user(i64 id) -> task&lt;result&lt;User, str&gt;&gt;
	///     #[get("/users")]       @fun search(str name, i32 page) -> task&lt;result&lt;list&lt;User&gt;, str&gt;&gt;   // остальные параметры GET → ?name=…&amp;page=…
	///     #[post("/users")]      @fun create(User u) -> task&lt;result&lt;User, str&gt;&gt;                      // первый «лишний» параметр → JSON-тело
	/// }
	/// UsersApi api = http_client.proxy&lt;UsersApi&gt;("https://api.example.com")
	/// match await api.user(1) { … }
	/// </code>
	/// Параметры, чьи имена есть в <c>{…}</c> пути, подставляются в адрес (с URL-кодированием).
	/// </summary>
	[KernStatic] public static T Proxy<T>(string baseUrl, [KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs) =>
		MakeProxy<T>(baseUrl, new Dictionary<string, string>(), vm, typeArgs);

	[KernStatic] public static T Proxy<T>(string baseUrl, KDict<string, string> headers, [KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs) =>
		MakeProxy<T>(baseUrl, headers.Items.ToDictionary(p => p.Key.AsStr, p => p.Value.AsStr), vm, typeArgs);

	private static T MakeProxy<T>(string baseUrl, Dictionary<string, string> headers, Interpreter vm, KType[] typeArgs)
	{
		var type = Kv.Last(typeArgs) as KClassType ?? throw new KernPanic("http_client.proxy: T должен быть абстрактным типом");
		var obj = new KObject(type, new KValue[vm.Info.Classes[type.DefIndex].FieldCount]);
		obj.Native = new KProxyState((v, fi, args) => Invoke(v, fi, args, baseUrl, headers));
		return Kv.As<T>(KValue.Obj(obj));
	}

	private static readonly string[] Verbs = ["get", "post", "put", "patch", "delete"];

	private static KValue Invoke(Interpreter vm, int fi, KValue[] args, string baseUrl, Dictionary<string, string> headers)
	{
		var m = Reflector.MethodOf(vm, fi);
		var attr = m.Attributes.FirstOrDefault(a => Verbs.Any(v => a.Is(v)))
			?? throw new KernPanic($"http-прокси: у метода {m.Name} нет атрибута #[get/post/put/patch/delete]");
		string verb = Verbs.First(v => attr.Is(v));
		string template = attr.Positional is [{ Kind: ValueKind.Str } p, ..] ? p.AsStr : "";

		// подстановка {имя} и сбор «лишних» параметров
		var path = new StringBuilder(template);
		var rest = new List<(string Name, KValue Value)>();
		for (int i = 0; i < m.Parameters.Count && i < args.Length; i++)
		{
			string name = m.Parameters[i].Name, token = "{" + name + "}";
			if (template.Contains(token)) path.Replace(token, Uri.EscapeDataString(vm.Stringify(args[i]).Trim('"')));
			else rest.Add((name, args[i]));
		}

		string? body = null, ctype = "";
		bool hasBody = verb is "post" or "put" or "patch";
		if (hasBody && rest.Count > 0)
		{
			(body, ctype) = EncodeBody(vm, rest[0].Value);
			rest.RemoveAt(0);
		}
		if (rest.Count > 0)
		{
			path.Append(template.Contains('?') ? '&' : '?');
			path.Append(string.Join('&', rest.Where(r => !r.Value.IsNilOrNone)
				.Select(r => Uri.EscapeDataString(r.Name) + "=" + Uri.EscapeDataString(vm.Stringify(r.Value).Trim('"')))));
		}

		// return: task<result<T, str>>
		var ret = m.ReturnType;
		if (ret is not KBuiltinGenericType { Builtin: "task", Args: [KBuiltinGenericType { Builtin: "result", Args: [var okType, _] }] })
			throw new KernPanic($"http-прокси: метод {m.Name} должен возвращать task<result<T, str>>, а возвращает {ret.Name}");

		var raw = SendAndConvert(vm, verb.ToUpperInvariant(), HttpRaw.Join(baseUrl, path.ToString()), body, ctype ?? "", headers, okType);
		return KValue.Obj(KTask.FromDotnet(raw, r => r));
	}

	private static async Task<KValue> SendAndConvert(Interpreter vm, string method, string url, string? body, string ctype, Dictionary<string, string> headers, KType okType)
	{
		var (text, error) = await HttpRaw.SendAsync(method, url, body, ctype, headers).ConfigureAwait(false);
		if (error is not null) return KValue.Obj(new KResultObject(false, KValue.Str(error)));
		var tcs = new TaskCompletionSource<KValue>();
		vm.Scheduler.Post(() =>
		{
			try { tcs.SetResult(KValue.Obj(new KResultObject(true, HttpRaw.ParseBody(vm, text!, okType)))); }
			catch (Exception e) when (e is not KernExit) { tcs.SetResult(KValue.Obj(new KResultObject(false, KValue.Str("разбор ответа: " + e.Message)))); }
		});
		return await tcs.Task.ConfigureAwait(false);
	}
}
