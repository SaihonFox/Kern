using System.Net;
using System.Text.Json;
using Kern.Binding.Attributes;
using Kern.Bytecode;
using Kern.Binding.Values;
using Kern.Runtime.Core;
using Kern.Runtime.Json;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Http;

/// <summary>
/// Контекст запроса: метод, путь, параметры маршрута, query, заголовки, cookie, тело,
/// пользователь и сессия (spec round 21).
/// </summary>
[KernType("ctx")]
public sealed class KernCtx(HttpListenerContext context, string body)
{
	internal HttpListenerContext Raw { get; } = context;
	internal Dictionary<string, string> RouteParams { get; set; } = [];
	internal Dictionary<string, KValue> Items { get; } = [];
	internal Interpreter? Vm { get; set; }
	internal List<string> RoleList { get; set; } = [];

	public string Method => Raw.Request.HttpMethod;
	public string Path => Raw.Request.Url?.AbsolutePath ?? "/";
	public string Body { get; } = body;
	public string Ip => Raw.Request.RemoteEndPoint?.Address.ToString() ?? "";
	public string? User { get; set; }
	public KernSession? Session { get; set; }
	public KernClaims? Claims { get; set; }
	public bool Cancelled { get; internal set; }

	public string? Param(string name) => RouteParams.GetValueOrDefault(name);

	public string? Query(string name) => Raw.Request.QueryString[name];

	public string? Header(string name) => Raw.Request.Headers[name];

	public string? Cookie(string name) =>
		Raw.Request.Cookies[name] is { } cookie ? Uri.UnescapeDataString(cookie.Value) : null;

	public bool HasRole(string role) => RoleList.Contains(role);

	internal static KernCtx Require(KNativeBox self) => self.Value switch
	{
		KernCtx ctx => ctx,
		_ => throw new KernPanic($"ctx: ожидался контекст, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Члены типа <c>ctx</c>.</summary>
[KernType("ctx")]
public static class CtxApi
{
	[KernProperty]
	public static string Method(KNativeBox self) => KernCtx.Require(self).Method;

	[KernProperty]
	public static string Path(KNativeBox self) => KernCtx.Require(self).Path;

	[KernProperty]
	public static string Body(KNativeBox self) => KernCtx.Require(self).Body;

	[KernProperty]
	public static string Ip(KNativeBox self) => KernCtx.Require(self).Ip;

	[KernProperty]
	public static string? User(KNativeBox self) => KernCtx.Require(self).User;

	[KernProperty]
	[KernReturn("list<str>")]
	public static KValue Roles(KNativeBox self) =>
		KernTypeHelpers.StringList(KernCtx.Require(self).RoleList);

	[KernProperty]
	public static KernSession? Session(KNativeBox self) => KernCtx.Require(self).Session;

	[KernProperty]
	public static KernClaims? Claims(KNativeBox self) => KernCtx.Require(self).Claims;

	[KernProperty]
	public static bool Cancelled(KNativeBox self) => KernCtx.Require(self).Cancelled;

	[KernMember]
	public static string? Param(KNativeBox self, string name) => KernCtx.Require(self).Param(name);

	[KernMember]
	public static string? Query(KNativeBox self, string name) => KernCtx.Require(self).Query(name);

	[KernMember]
	public static string? Header(KNativeBox self, string name) => KernCtx.Require(self).Header(name);

	[KernMember]
	public static string? Cookie(KNativeBox self, string name) => KernCtx.Require(self).Cookie(name);

	[KernMember]
	public static bool HasRole(KNativeBox self, string role) => KernCtx.Require(self).HasRole(role);

	/// <summary><c>ctx.json&lt;T&gt;()</c>: разобрать JSON тела запроса в <c>T</c>.</summary>
	[KernMember]
	[KernReturn("result<T, str>")]
	public static KValue Json<T>(KNativeBox self, Interpreter interpreter, [KernTypeArgs] KType[] types)
	{
		KernCtx ctx = KernCtx.Require(self);
		KType type = types.Length > 0 ? types[^1] : interpreter.Types.Prim(PrimKind.Object);
		try
		{
			KValue value = JsonApi.ParseInto(ctx.Body, type, interpreter);
			return KernResults.Ok(interpreter, type, value);
		}
		catch (Exception exception) when (exception is KernPanic or JsonException)
		{
			return KernResults.Err(
				interpreter,
				interpreter.Types.Prim(PrimKind.Str),
				KValue.Str(exception.Message));
		}
	}

	/// <summary>Положить значение в контекст для последующих middleware/обработчика.</summary>
	[KernMember]
	public static void Set(KNativeBox self, string key, KValue value) =>
		KernCtx.Require(self).Items[key] = value;

	/// <summary>Достать значение из контекста; <c>T?</c> (nil, если не задано).</summary>
	[KernMember]
	[KernReturn("T?")]
	public static KValue Get<T>(KNativeBox self, string key, [KernTypeArgs] KType[] types)
	{
		_ = types;
		return KernCtx.Require(self).Items.TryGetValue(key, out KValue value) ? value : KValue.Nil;
	}
}
