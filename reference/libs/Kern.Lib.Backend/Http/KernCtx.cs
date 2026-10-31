using System.Net;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Http;

/// <summary>Контекст запроса: метод, путь, параметры маршрута, query, заголовки, cookie, тело, пользователь и сессия.</summary>
[KernType("ctx")]
public sealed class KernCtx(HttpListenerContext context, string body)
{
	internal HttpListenerContext Raw { get; } = context;
	internal Dictionary<string, string> RouteParams { get; set; } = [];
	internal Dictionary<string, KValue> Items { get; } = [];
	internal Interpreter? Vm { get; set; }
	internal List<string> RoleList { get; set; } = [];

	[KernProperty] public string Method => Raw.Request.HttpMethod;
	[KernProperty] public string Path => Raw.Request.Url?.AbsolutePath ?? "/";
	[KernProperty] public string Body { get; } = body;
	[KernProperty] public string Ip => Raw.Request.RemoteEndPoint?.Address.ToString() ?? "";

	/// <summary>Имя пользователя, установленное auth-middleware (nil — аноним).</summary>
	[KernProperty] public string? User { get; set; }
	[KernProperty] public KList<string> Roles => Kv.Strs(RoleList);
	[KernProperty] public KernSession? Session { get; set; }

	[KernMember] public string? Param(string name) => RouteParams.GetValueOrDefault(name);
	[KernMember] public string? Query(string name) => Raw.Request.QueryString[name];
	[KernMember] public string? Header(string name) => Raw.Request.Headers[name];
	[KernMember] public string? Cookie(string name) => Raw.Request.Cookies[name] is { } c ? Uri.UnescapeDataString(c.Value) : null;
	[KernMember] public bool HasRole(string role) => RoleList.Contains(role);

	/// <summary>Разбор тела как JSON в тип T: `ctx.json&lt;user_dto&gt;()`.</summary>
	[KernMember]
	public KResult<T, string> Json<T>([KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs)
	{
		try { return KResult<T, string>.Ok(Kern.Runtime.Json.JsonApi.FromJsonText(vm, Body, Kv.Last(typeArgs))); }
		catch (Exception e) when (e is KernPanic or System.Text.Json.JsonException) { return KResult<T, string>.Err(KValue.Str(e.Message)); }
	}

	/// <summary>Состояние запроса для передачи между middleware и обработчиком.</summary>
	[KernMember] public void Set<T>(string key, T value) => Items[key] = Kv.Of(value);
	[KernMember] public T? Get<T>(string key) => Items.TryGetValue(key, out var v) ? Kv.As<T?>(v) : Kv.As<T?>(KValue.Nil);
}
