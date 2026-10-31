using System.Globalization;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Bytecode;
using Kern.Lib.Backend.Http;
using Kern.Lib.Backend.Middleware;
using Kern.Lib.Di.Container;
using Kern.Runtime.Core;
using Kern.Runtime.Json;
using Kern.Vm.Execution;
using Kern.Vm.Reflection;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Routing;

/// <summary>
/// Атрибутные контроллеры:
/// <code>
/// #[controller("/users")]
/// type users_controller(user_repo repo) {
///     #[get("/")]            fun list -> res res.json(repo.all())
///     #[get("/{id}")]        fun show(i32 id) -> user? repo.find(id)          // nil → 404
///     #[post("/")] #[auth]   fun create(user_dto dto) -> res res.json(repo.add(dto))
///     #[delete("/{id}")] #[roles("admin")] fun remove(i32 id) { repo.remove(id) }   // nil → 204
/// }
/// </code>
/// Параметры метода связываются по типу и имени: `ctx`/`session`/`claims` — из запроса; примитивы — из параметров маршрута,
/// затем из query; остальные типы — из JSON-тела. Контроллер создаётся на каждый запрос (с зависимостями из `services`).
/// </summary>
public sealed partial class KernApp
{
	private static readonly string[] HttpAttributes = ["get", "post", "put", "patch", "delete"];
	private KernContainer? _services;

	/// <summary>Контейнер зависимостей (kern.di) для создания контроллеров: на каждый запрос открывается scope.</summary>
	[KernMember] public void Services(KernContainer container) => _services = container;

	/// <summary>Регистрирует один контроллер: `a.controller&lt;users_controller&gt;()`.</summary>
	[KernMember]
	public void Controller<T>([KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs) =>
		RegisterController(Reflector.TypeOf(vm, Kv.Last(typeArgs)));

	/// <summary>Регистрирует все типы с `#[controller]`; возвращает их число.</summary>
	[KernMember]
	public int ScanControllers([KernVm] Interpreter vm)
	{
		var types = Reflector.TypesWithAttribute(vm, "controller");
		foreach (var t in types) RegisterController(t);
		return types.Count;
	}

	private void RegisterController(TypeInfo type)
	{
		var prefix = type.GetAttribute("controller") is { Positional.Count: > 0 } c ? c.Positional[0].AsStr : "";
		var classAuth = type.HasAttribute("auth");
		var classRoles = RolesOf(type.GetAttribute("roles"));

		foreach (var method in type.Methods.Where(m => !m.IsConstructor && !m.IsStatic))
			foreach (var attr in method.Attributes.Where(a => HttpAttributes.Any(a.Is)))
			{
				var verb = HttpAttributes.First(attr.Is).ToUpperInvariant();
				var path = attr.Positional.Count > 0 ? attr.Positional[0].AsStr : "";
				var m = method;
				var route = Add(verb, RoutePattern.Join(prefix, path), new Terminal(ctx => Invoke(type, m, ctx)));
				if (classAuth || method.HasAttribute("auth") || classRoles.Count > 0 || method.HasAttribute("roles"))
					route.Extra.Add(Guarded(classRoles.Concat(RolesOf(method.GetAttribute("roles"))).ToList()));
			}
	}

	private static List<string> RolesOf(AttributeInfo? attr) =>
		attr is null ? [] : [.. attr.Positional.Where(v => v.Kind == ValueKind.Str).Select(v => v.AsStr)];

	/// <summary>`#[auth]`/`#[roles]`: без пользователя — 401, без нужной роли — 403.</summary>
	private static Layer Guarded(List<string> roles) => (ctx, next) =>
	{
		if (ctx.User is null) return KernRes.Unauthorized();
		if (roles.Count > 0 && !roles.Any(ctx.HasRole)) return KernRes.Forbidden();
		return next.Next();
	};

	private KernRes Invoke(TypeInfo type, MethodInfo method, KernCtx ctx)
	{
		var vm = ctx.Vm!;
		var scope = _services?.Scope();
		try
		{
			var instance = scope is null ? type.CreateInstance() : scope.CreateByType(vm, type.Type);
			var args = Bind(vm, method, ctx);
			return Adapt(vm, method.Invoke(instance, args), method.ReturnType);
		}
		catch (BindingError e) { return KernRes.BadRequest(e.Message); }
		finally { scope?.Dispose(vm); }
	}

	// ------------------------------------------------------------- связывание параметров

	private sealed class BindingError(string message) : Exception(message);

	private static KValue[] Bind(Interpreter vm, MethodInfo method, KernCtx ctx)
	{
		var args = new List<KValue>();
		foreach (var p in method.Parameters)
		{
			var type = p.Type is KNullableType n ? n.Inner : p.Type;
			var optional = p.HasDefault || p.Type is KNullableType;

			if (type is KNativeType native && native.ClrType == typeof(KernCtx)) { args.Add(KValue.Obj(ctx)); continue; }
			if (type is KNativeType s && s.ClrType == typeof(KernSession)) { args.Add(ctx.Session is { } ss ? KValue.Obj(ss) : KValue.Nil); continue; }

			if (type is KPrimType prim)
			{
				var raw = ctx.Param(p.Name) ?? ctx.Query(p.Name);
				if (raw is null)
				{
					if (p.Type is KNullableType) { args.Add(KValue.Nil); continue; }
					if (p.HasDefault) break;
					throw new BindingError($"не хватает параметра '{p.Name}'");
				}
				args.Add(Parse(prim.Kind, raw, p.Name));
				continue;
			}

			// сложный тип — JSON-тело
			if (ctx.Body.Length == 0)
			{
				if (p.Type is KNullableType) { args.Add(KValue.Nil); continue; }
				if (optional) break;
				throw new BindingError($"ожидалось тело запроса ({type.Name})");
			}
			try { args.Add(JsonApi.FromJsonText(vm, ctx.Body, type)); }
			catch (Exception e) when (e is KernPanic or System.Text.Json.JsonException)
			{
				throw new BindingError($"тело запроса не соответствует {type.Name}: {e.Message}");
			}
		}
		return [.. args];
	}

	private static KValue Parse(PrimKind kind, string raw, string name)
	{
		var inv = CultureInfo.InvariantCulture;
		switch (kind)
		{
			case PrimKind.Str: return KValue.Str(raw);
			case PrimKind.Bool when bool.TryParse(raw, out var b): return KValue.Bool(b);
			case PrimKind.F16 or PrimKind.F32 or PrimKind.F64 when double.TryParse(raw, NumberStyles.Float, inv, out var d): return KValue.F64(d);
			case PrimKind.I8 or PrimKind.I16 or PrimKind.I32 or PrimKind.I64 or PrimKind.U8 or PrimKind.U16 or PrimKind.U32 or PrimKind.U64
				when long.TryParse(raw, NumberStyles.Integer, inv, out var l): return KValue.I64(l);
			default: throw new BindingError($"параметр '{name}': '{raw}' не подходит к типу {kind.ToString().ToLowerInvariant()}");
		}
	}

	// ------------------------------------------------------------- результат

	private static KernRes Adapt(Interpreter vm, KValue value, KType declared)
	{
		if (value.Ref is KernRes res) return res;
		if (value.IsNilOrNone || value.Kind == ValueKind.Void) return declared is KNullableType ? KernRes.NotFound() : KernRes.NoContent();
		if (value.Kind == ValueKind.Str) return KernRes.Text(value.AsStr);
		if (value.Ref is KResultObject r)
		{
			if (r.IsOk) return Adapt(vm, r.Value, declared);
			return KernRes.BadRequest(r.Value.Kind == ValueKind.Str ? r.Value.AsStr : JsonApi.ToStr(vm, r.Value));
		}
		return KernRes.Json(value, vm);
	}
}
