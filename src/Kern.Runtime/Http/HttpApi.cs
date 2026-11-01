using System.Net.Http.Headers;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Bytecode;
using Kern.Runtime.Core;
using Kern.Runtime.Json;
using Kern.Vm.Async;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Http;

/// <summary>Ответ HTTP-запроса (spec round 5).</summary>
[KernType("http_reply")]
public sealed class KernHttpReply(int status, string body, IReadOnlyDictionary<string, string> headers)
{
	public int Status { get; } = status;

	public string Body { get; } = body;

	public IReadOnlyDictionary<string, string> Headers { get; } = headers;

	public string? Header(string name) =>
		Headers.TryGetValue(name, out string? value) ? value : null;
}

/// <summary>Члены <c>http_reply</c>.</summary>
[KernType("http_reply")]
public static class HttpReplyApi
{
	[KernProperty]
	public static int Status(KNativeBox self) => Require(self).Status;

	[KernProperty]
	public static string Body(KNativeBox self) => Require(self).Body;

	/// <summary><c>header(name) -&gt; str?</c>: значение заголовка без учёта регистра.</summary>
	[KernMember]
	public static string? Header(KNativeBox self, string name) => Require(self).Header(name);

	/// <summary><c>headers</c>: все заголовки ответа.</summary>
	[KernProperty]
	public static KValue Headers(KNativeBox self)
	{
		var dict = new Kern.Vm.Values.KDict(
			null,
			null);
		foreach (var (name, value) in Require(self).Headers)
			dict.Set(KValue.Str(name), KValue.Str(value));
		return KValue.Obj(dict);
	}

	private static KernHttpReply Require(KNativeBox self) => self.Value switch
	{
		KernHttpReply reply => reply,
		_ => throw new KernPanic($"http_reply: ожидался ответ, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>
/// <c>kern.http</c> (spec round 5): асинхронный HTTP-клиент. Ошибки сети и статусы вне
/// 2xx — <c>Err</c> с русским текстом, а не паника.
/// </summary>
[KernType("http")]
public static class HttpApi
{
	internal static readonly HttpClient Shared = new(new HttpClientHandler { UseProxy = false })
	{
		Timeout = TimeSpan.FromSeconds(30),
	};

	/// <summary><c>await http.get(url)</c>: <c>result&lt;http_reply, str&gt;</c>.</summary>
	[KernStatic]
	[KernReturn("task<result<http_reply, str>>")]
	public static KValue Get(Interpreter interpreter, string url) =>
		Start(interpreter, "GET", url, null, null, target: null);

	/// <summary><c>await http.post(url, body)</c>: текст уходит как есть.</summary>
	[KernStatic]
	[KernReturn("task<result<http_reply, str>>")]
	public static KValue Post(Interpreter interpreter, string url, string body) =>
		Start(interpreter, "POST", url, body, null, target: null);

	/// <summary><c>await http.put(url, body)</c>.</summary>
	[KernStatic]
	[KernReturn("task<result<http_reply, str>>")]
	public static KValue Put(Interpreter interpreter, string url, string body) =>
		Start(interpreter, "PUT", url, body, null, target: null);

	/// <summary><c>await http.delete(url)</c>.</summary>
	[KernStatic]
	[KernReturn("task<result<http_reply, str>>")]
	public static KValue Delete(Interpreter interpreter, string url) =>
		Start(interpreter, "DELETE", url, null, null, target: null);

	/// <summary><c>await http.post_json(url, value)</c>: тело — JSON значения.</summary>
	[KernStatic]
	[KernName("post_json")]
	[KernReturn("task<result<http_reply, str>>")]
	public static KValue PostJson(Interpreter interpreter, string url, KValue value) =>
		Start(interpreter, "POST", url, JsonApi.Serialize(value), null, target: null);

	/// <summary>
	/// <c>await http.request(method, url[, body])</c>: произвольный метод; тело — текст.
	/// </summary>
	[KernStatic]
	[KernReturn("task<result<http_reply, str>>")]
	public static KValue Request(Interpreter interpreter, string method, string url, string body) =>
		Start(interpreter, method, url, body, null, target: null);

	/// <summary>
	/// Общая точка: выполнить запрос, разобрать ответ в <paramref name="target"/>
	/// (null — вернуть <c>http_reply</c>) и завершить задачу в потоке VM.
	/// </summary>
	internal static KValue Start(
		Interpreter interpreter,
		string method,
		string url,
		string? body,
		IReadOnlyDictionary<string, string>? headers,
		KType? target)
	{
		var task = new KTask();
		KType valueType = target ?? ReplyType(interpreter);
		KType errorType = interpreter.Types.Prim(PrimKind.Str);
		KClassType okType = KernResults.OkType(interpreter, valueType);
		KClassType errType = KernResults.ErrType(interpreter, errorType);

		_ = Task.Run(async () =>
		{
			try
			{
				using var request = new HttpRequestMessage(new HttpMethod(method), url);
				if (body is not null)
					request.Content = new StringContent(body, Encoding.UTF8, "application/json");
				if (headers is not null)
					foreach (var (name, value) in headers)
						request.Headers.TryAddWithoutValidation(name, value);

				using HttpResponseMessage response = await Shared.SendAsync(request).ConfigureAwait(false);
				string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				var reply = new KernHttpReply(
					(int)response.StatusCode,
					text,
					response.Headers.Concat(response.Content.Headers)
						.ToDictionary(pair => pair.Key, pair => string.Join(", ", pair.Value), StringComparer.OrdinalIgnoreCase));

				if (!response.IsSuccessStatusCode)
				{
					var failure = KernResults.Err(
						interpreter,
						errorType,
						KValue.Str($"HTTP {(int)response.StatusCode}: {text}"));
					interpreter.Scheduler.EndExternal(() => task.Complete(failure));
					return;
				}

				interpreter.Scheduler.EndExternal(() =>
				{
					try
					{
						KValue payload = target is null
							? KValue.Obj(new KNativeBox(reply))
							: ParseBody(text, target, interpreter);
						task.Complete(KernResults.Make(okType, payload));
					}
					catch (KernPanic panic)
					{
						task.Complete(KernResults.Make(errType, KValue.Str(panic.Message)));
					}
				});
			}
			catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
			{
				interpreter.Scheduler.EndExternal(() =>
					task.Complete(KernResults.Make(errType, KValue.Str($"http: {exception.Message}"))));
			}
		});

		return KValue.Obj(task);
	}

	internal static KValue ParseBody(string text, KType target, Interpreter interpreter) =>
		target is KPrimType { Prim: PrimKind.Str }
			? KValue.Str(text)
			: JsonApi.ParseInto(text, target, interpreter);

	private static KType ReplyType(Interpreter interpreter)
	{
		foreach (TypeDef type in interpreter.Module.TypePool)
			if (type.Name == "http_reply" && type.GenericParams.Count == 0)
				return interpreter.Types.Named(type);
		return interpreter.Types.Prim(PrimKind.Object);
	}
}

/// <summary>Типизированный HTTP-клиент с базовым адресом (spec round 10, вариант A).</summary>
[KernType("http_client")]
public sealed class KernHttpClient(string baseUrl)
{
	private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

	public string BaseUrl { get; } = baseUrl;

	public IReadOnlyDictionary<string, string> Headers => _headers;

	public void SetHeader(string name, string value) => _headers[name] = value;

	public void SetBearer(string token) => _headers["Authorization"] = $"Bearer {token}";

	public string Url(string path) =>
		path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
		path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
			? path
			: BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

	public string? Body(KValue value) => value.Kind switch
	{
		ValueKind.Nil or ValueKind.None => null,
		ValueKind.Str => value.AsStr,
		_ => JsonApi.Serialize(value),
	};
}

/// <summary>Фабрика и члены <c>http_client</c> (вариант A).</summary>
[KernType("http_client")]
public static class HttpClientApi
{
	[KernCtor]
	[KernName("http_client")]
	[KernReturn("http_client")]
	public static KValue Create(string baseUrl) =>
		KValue.Obj(new KNativeBox(new KernHttpClient(baseUrl)));

	/// <summary><c>header(name, value)</c>: добавить заголовок запросов.</summary>
	[KernMember]
	public static void Header(KNativeBox self, string name, string value) => Require(self).SetHeader(name, value);

	/// <summary><c>bearer(token)</c>: заголовок <c>Authorization: Bearer …</c>.</summary>
	[KernMember]
	public static void Bearer(KNativeBox self, string token) => Require(self).SetBearer(token);

	/// <summary><c>http_client.proxy&lt;Api&gt;(base)</c>: типизированный клиент по атрибутам.</summary>
	[KernStatic]
	[KernName("proxy")]
	[KernReturn("T")]
	public static KValue Proxy<T>(Interpreter interpreter, string baseUrl, [KernTypeArgs] KType[] typeArguments)
	{
		if (typeArguments.Length != 1 || typeArguments[0] is not KClassType @class)
			throw new KernPanic("http_client.proxy: нужен один аргумент типа — абстрактный @type");

		KValue[] fields = new KValue[@class.Definition.Fields.Count];
		Array.Fill(fields, KValue.None);
		var target = new KObject(@class, fields) { Native = new HttpProxy(baseUrl) };
		return KValue.Obj(target);
	}

	/// <summary><c>await api.get&lt;T&gt;(path)</c>.</summary>
	[KernMember]
	[KernReturn("task<result<T, str>>")]
	public static KValue Get<T>(Interpreter interpreter, KNativeBox self, string path, [KernTypeArgs] KType[] typeArguments) =>
		Request(interpreter, self, "GET", path, KValue.Nil, typeArguments);

	/// <summary><c>await api.delete&lt;T&gt;(path)</c>.</summary>
	[KernMember]
	[KernReturn("task<result<T, str>>")]
	public static KValue Delete<T>(Interpreter interpreter, KNativeBox self, string path, [KernTypeArgs] KType[] typeArguments) =>
		Request(interpreter, self, "DELETE", path, KValue.Nil, typeArguments);

	/// <summary><c>await api.post&lt;T&gt;(path, body)</c>.</summary>
	[KernMember]
	[KernReturn("task<result<T, str>>")]
	public static KValue Post<T>(Interpreter interpreter, KNativeBox self, string path, KValue body, [KernTypeArgs] KType[] typeArguments) =>
		Request(interpreter, self, "POST", path, body, typeArguments);

	/// <summary><c>await api.put&lt;T&gt;(path, body)</c>.</summary>
	[KernMember]
	[KernReturn("task<result<T, str>>")]
	public static KValue Put<T>(Interpreter interpreter, KNativeBox self, string path, KValue body, [KernTypeArgs] KType[] typeArguments) =>
		Request(interpreter, self, "PUT", path, body, typeArguments);

	/// <summary><c>await api.patch&lt;T&gt;(path, body)</c>.</summary>
	[KernMember]
	[KernReturn("task<result<T, str>>")]
	public static KValue Patch<T>(Interpreter interpreter, KNativeBox self, string path, KValue body, [KernTypeArgs] KType[] typeArguments) =>
		Request(interpreter, self, "PATCH", path, body, typeArguments);

	private static KValue Request(
		Interpreter interpreter,
		KNativeBox self,
		string method,
		string path,
		KValue body,
		KType[] typeArguments)
	{
		KernHttpClient client = Require(self);
		KType target = typeArguments.Length > 0 ? typeArguments[0] : interpreter.Types.Prim(PrimKind.Object);
		return HttpApi.Start(
			interpreter,
			method,
			client.Url(path),
			body.IsNilOrNone ? null : client.Body(body),
			client.Headers,
			target);
	}

	private static KernHttpClient Require(KNativeBox self) => self.Value switch
	{
		KernHttpClient client => client,
		_ => throw new KernPanic($"http_client: ожидался клиент, получено {self.Value.GetType().Name}"),
	};
}
