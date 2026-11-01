using System.Text;
using Kern.Binding.Attributes;
using Kern.Runtime.Json;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Http;

/// <summary>Ответ: <c>res.json(x)</c>, <c>res.text(".")</c>, <c>res.send(404, "…")</c>.</summary>
[KernType("res")]
public sealed class KernRes
{
	internal byte[] Bytes { get; set; } = [];
	internal Dictionary<string, string> Headers { get; } = [];
	internal List<string> Cookies { get; } = [];

	/// <summary>Ответ-перехват (WebSocket/SSE): тело не отправляется обычным путём.</summary>
	internal Action<Interpreter>? Takeover { get; set; }

	public int Status { get; set; } = 200;
	public string ContentType { get; set; } = "text/plain; charset=utf-8";

	public string Body
	{
		get => Encoding.UTF8.GetString(Bytes);
		set => Bytes = Encoding.UTF8.GetBytes(value);
	}

	internal static KernRes Make(int status, string body, string contentType) =>
		new() { Status = status, ContentType = contentType, Bytes = Encoding.UTF8.GetBytes(body) };

	internal static KernRes Handover(Action<Interpreter> takeover) => new() { Takeover = takeover };

	internal static KernRes Error(int code, string message) =>
		Make(
			code,
			"{\"error\":" + System.Text.Json.JsonSerializer.Serialize(message) + "}",
			"application/json; charset=utf-8");

	internal KernRes WithHeader(string name, string value)
	{
		Headers[name] = value;
		return this;
	}

	internal KernRes Cookie(string name, string value, int maxAge = 0)
	{
		Cookies.Add(
			$"{name}={Uri.EscapeDataString(value)}; Path=/; HttpOnly; SameSite=Lax" +
			(maxAge > 0 ? $"; Max-Age={maxAge}" : ""));
		return this;
	}

	internal static KernRes File(string path)
	{
		if (!System.IO.File.Exists(path))
			return Error(404, "не найдено");
		return new KernRes { Bytes = System.IO.File.ReadAllBytes(path), ContentType = MimeOf(path) };
	}

	internal static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
	{
		".html" or ".htm" => "text/html; charset=utf-8",
		".css" => "text/css; charset=utf-8",
		".js" or ".mjs" => "text/javascript; charset=utf-8",
		".json" => "application/json; charset=utf-8",
		".txt" => "text/plain; charset=utf-8",
		".svg" => "image/svg+xml",
		".png" => "image/png",
		".jpg" or ".jpeg" => "image/jpeg",
		".gif" => "image/gif",
		".ico" => "image/x-icon",
		".wasm" => "application/wasm",
		".pdf" => "application/pdf",
		_ => "application/octet-stream",
	};

	internal static KernRes Require(KNativeBox self) => self.Value switch
	{
		KernRes res => res,
		_ => throw new KernPanic($"res: ожидался ответ, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Статические фабрики и члены типа <c>res</c>.</summary>
[KernType("res")]
public static class ResApi
{
	[KernStatic]
	public static KernRes Text(string body) => KernRes.Make(200, body, "text/plain; charset=utf-8");

	[KernStatic]
	public static KernRes Html(string body) => KernRes.Make(200, body, "text/html; charset=utf-8");

	[KernStatic]
	public static KernRes Json(KValue value, Interpreter interpreter) =>
		KernRes.Make(200, JsonApi.ToStr(value), "application/json; charset=utf-8");

	[KernStatic]
	[KernName("json_status")]
	public static KernRes JsonStatus(int code, KValue value, Interpreter interpreter) =>
		KernRes.Make(code, JsonApi.ToStr(value), "application/json; charset=utf-8");

	[KernStatic]
	[KernName("send")]
	public static KernRes Send(int code, string body) =>
		KernRes.Make(code, body, "text/plain; charset=utf-8");

	[KernStatic]
	public static KernRes Redirect(string location) =>
		KernRes.Make(302, "", "text/plain").WithHeader("Location", location);

	[KernStatic]
	[KernName("no_content")]
	public static KernRes NoContent() => KernRes.Make(204, "", "text/plain");

	[KernStatic]
	[KernName("not_found")]
	public static KernRes NotFound() => KernRes.Error(404, "не найдено");

	[KernStatic]
	[KernName("error")]
	public static KernRes ErrorJson(int code, string message) => KernRes.Error(code, message);

	[KernStatic]
	[KernName("bad_request")]
	public static KernRes BadRequest(string message) => KernRes.Error(400, message);

	[KernStatic]
	[KernName("unauthorized")]
	public static KernRes Unauthorized() => KernRes.Error(401, "требуется авторизация");

	[KernStatic]
	[KernName("unauthorized")]
	public static KernRes Unauthorized(string message) => KernRes.Error(401, message);

	[KernStatic]
	[KernName("forbidden")]
	public static KernRes Forbidden() => KernRes.Error(403, "доступ запрещён");

	[KernStatic]
	[KernName("forbidden")]
	public static KernRes Forbidden(string message) => KernRes.Error(403, message);

	[KernStatic]
	public static KernRes File(string path) => KernRes.File(path);

	[KernProperty]
	public static int Status(KNativeBox self) => KernRes.Require(self).Status;

	[KernProperty]
	public static void Status(KNativeBox self, int value) => KernRes.Require(self).Status = value;

	[KernProperty]
	public static string ContentType(KNativeBox self) => KernRes.Require(self).ContentType;

	[KernProperty]
	public static void ContentType(KNativeBox self, string value) => KernRes.Require(self).ContentType = value;

	[KernProperty]
	public static string Body(KNativeBox self) => KernRes.Require(self).Body;

	[KernProperty]
	public static void Body(KNativeBox self, string value) => KernRes.Require(self).Body = value;

	[KernMember]
	public static KernRes Header(KNativeBox self, string name, string value) =>
		KernRes.Require(self).WithHeader(name, value);

	[KernMember]
	public static KernRes Cookie(KNativeBox self, string name, string value) =>
		Cookie(self, name, value, 0);

	[KernMember]
	public static KernRes Cookie(KNativeBox self, string name, string value, int maxAge) =>
		KernRes.Require(self).Cookie(name, value, maxAge);
}
