using System.Text;
using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Http;

/// <summary>Ответ: `res.json(x)`, `res.text("…")`, `res.send(404, "нет")`, `res.redirect("/")`, `res.file("a.png")`.</summary>
[KernType("res")]
public sealed class KernRes
{
	internal byte[] Bytes { get; set; } = [];
	internal Dictionary<string, string> Headers { get; } = [];
	internal List<string> Cookies { get; } = [];
	/// <summary>Если задано — соединение переходит под управление приложения (WebSocket/SSE), обычный ответ не отправляется.</summary>
	internal Action<Kern.Vm.Execution.Interpreter>? Takeover { get; set; }

	[KernProperty] public int Status { get; set; } = 200;
	[KernProperty] public string ContentType { get; set; } = "text/plain; charset=utf-8";
	[KernProperty] public string Body
	{
		get => Encoding.UTF8.GetString(Bytes);
		set => Bytes = Encoding.UTF8.GetBytes(value);
	}

	internal static KernRes Make(int status, string body, string contentType) =>
		new() { Status = status, ContentType = contentType, Bytes = Encoding.UTF8.GetBytes(body) };

	internal static KernRes Handover(Action<Kern.Vm.Execution.Interpreter> takeover) => new() { Takeover = takeover };

	[KernStatic] public static KernRes Text(string body) => Make(200, body, "text/plain; charset=utf-8");
	[KernStatic] public static KernRes Html(string body) => Make(200, body, "text/html; charset=utf-8");
	[KernStatic] public static KernRes Json(KValue value, [KernVm] Interpreter vm) => Make(200, Kern.Runtime.Json.JsonApi.ToStr(vm, value), "application/json; charset=utf-8");
	[KernStatic] public static KernRes JsonStatus(int code, KValue value, [KernVm] Interpreter vm) => Make(code, Kern.Runtime.Json.JsonApi.ToStr(vm, value), "application/json; charset=utf-8");
	[KernStatic("send")] public static KernRes Send(int code, string body) => Make(code, body, "text/plain; charset=utf-8");
	[KernStatic] public static KernRes Redirect(string location) => Make(302, "", "text/plain").WithHeader("Location", location);
	[KernStatic] public static KernRes NoContent() => Make(204, "", "text/plain");
	[KernStatic] public static KernRes NotFound() => Error(404, "не найдено");
	/// <summary>JSON-ошибка {"error": …} с произвольным кодом.</summary>
	[KernStatic("error")] public static KernRes ErrorJson(int code, string message) => Error(code, message);
	[KernStatic] public static KernRes BadRequest(string message) => Error(400, message);
	[KernStatic] public static KernRes Unauthorized(string message = "требуется авторизация") => Error(401, message);
	[KernStatic] public static KernRes Forbidden(string message = "доступ запрещён") => Error(403, message);

	/// <summary>Отдаёт файл целиком (тип по расширению); 404, если файла нет.</summary>
	[KernStatic]
	public static KernRes File(string path)
	{
		if (!System.IO.File.Exists(path)) return NotFound();
		return new KernRes { Bytes = System.IO.File.ReadAllBytes(path), ContentType = MimeOf(path) };
	}

	/// <summary>JSON-ошибка вида {"error": "…"}.</summary>
	internal static KernRes Error(int code, string message) =>
		Make(code, "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(message) + "}", "application/json; charset=utf-8");

	[KernMember] public KernRes Header(string name, string value) => WithHeader(name, value);

	/// <summary>Set-Cookie с HttpOnly и SameSite=Lax; <paramref name="maxAge"/> в секундах (0 — сессионная).</summary>
	[KernMember]
	public KernRes Cookie(string name, string value, int maxAge = 0)
	{
		Cookies.Add($"{name}={Uri.EscapeDataString(value)}; Path=/; HttpOnly; SameSite=Lax" + (maxAge > 0 ? $"; Max-Age={maxAge}" : ""));
		return this;
	}

	internal KernRes WithHeader(string name, string value)
	{
		Headers[name] = value;
		return this;
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
}
