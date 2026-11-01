using System.Globalization;
using System.Text.Json;
using Kern.Binding.Values;
using Kern.Bytecode;
using Kern.Lib.Backend.Http;
using Kern.Lib.Backend.Middleware;
using Kern.Lib.Di;
using Kern.Runtime.Json;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Routing;

/// <summary>
/// Контроллеры: <c>#[controller("/users")] type users(...) { #[get("/{id}")] fun show(...) }</c>.
/// Параметры берутся из пути/query (примитивы) или JSON-тела (остальные типы).
/// </summary>
public sealed partial class KernApp
{
	private static readonly string[] HttpAttributes = ["get", "post", "put", "patch", "delete"];
	private readonly HashSet<TypeDef> _controllers = [];
	private KernContainer? _services;

	internal void SetServices(KernContainer container) => _services = container;

	internal void RegisterController(Interpreter interpreter, KType type)
	{
		if (type is not KClassType classType)
			throw new KernPanic($"controller: ожидался тип контроллера, получено '{type}'");
		TypeDef definition = classType.Definition;
		if (!_controllers.Add(definition))
			return;
		if (definition.Kind == TypeDefKind.Union || (definition.Flags & MemberFlags.Abstract) != 0)
			throw new KernPanic($"controller: тип '{definition.Name}' не является конкретным");

		string prefix = AttributeString(definition.Attributes, "controller") ?? "";
		bool classAuth = HasAttribute(definition.Attributes, "auth");
		List<string> classRoles = RolesOf(definition.Attributes, "roles");

		foreach (int index in definition.Methods)
		{
			FunctionDef method = interpreter.Module.FunctionPool[index];
			if (method.IsNative || (method.Flags & (MemberFlags.Static | MemberFlags.Ctor)) != 0)
				continue;
			AttributeUse? http = method.Attributes.FirstOrDefault(attribute => HttpAttributes.Contains(attribute.Name));
			if (http is null)
				continue;

			string verb = http.Name.ToUpperInvariant();
			string path = http.Args.Count > 0 && http.Args[0].Kind == KConstKind.Str
				? http.Args[0].AsStr
				: "";
			RouteEntry route = AddHttp(
				verb,
				RoutePattern.Join(prefix, path),
				ctx => InvokeController(interpreter, classType, method, ctx));
			if (classAuth || HasAttribute(method.Attributes, "auth") ||
				classRoles.Count > 0 || HasAttribute(method.Attributes, "roles"))
			{
				route.Extra.Add(Guarded([.. classRoles, .. RolesOf(method.Attributes, "roles")]));
			}
		}
	}

	internal int ScanControllers(Interpreter interpreter)
	{
		var found = 0;
		foreach (TypeDef definition in interpreter.Module.TypePool)
		{
			if (!HasAttribute(definition.Attributes, "controller"))
				continue;
			if (definition.Kind == TypeDefKind.Union || (definition.Flags & MemberFlags.Abstract) != 0)
				continue;
			RegisterController(interpreter, interpreter.Types.Named(definition));
			found++;
		}

		return found;
	}

	private static Layer Guarded(List<string> roles) => (ctx, next) =>
	{
		if (ctx.User is null)
			return KernRes.Error(401, "требуется авторизация");
		if (roles.Count > 0 && !roles.Any(ctx.HasRole))
			return KernRes.Error(403, "доступ запрещён");
		return next.Next();
	};

	private KernRes InvokeController(
		Interpreter interpreter,
		KClassType type,
		FunctionDef method,
		KernCtx ctx)
	{
		KernContainer? scope = _services?.CreateScope();
		try
		{
			scope ??= KernContainer.CreateRoot();
			KValue instance = scope.CreateByType(interpreter, type);
			KValue[] arguments = BindControllerArguments(interpreter, type, method, ctx);
			KValue result = interpreter.RunValue(KValue.Obj(method), [instance, .. arguments]);
			if (result.Ref is Kern.Vm.Async.KTask task)
				result = task.GetResult(interpreter);
			return Adapt(interpreter, result, method.ReturnType);
		}
		catch (BindingError error)
		{
			return KernRes.Error(400, error.Message);
		}
		finally
		{
			scope?.DisposeAll(interpreter);
		}
	}

	private static KValue[] BindControllerArguments(
		Interpreter interpreter,
		KClassType owner,
		FunctionDef method,
		KernCtx ctx)
	{
		string[] names = ParameterNames(method);
		var arguments = new List<KValue>(Math.Max(0, method.ParamCount - 1));
		for (var index = 1; index < method.ParamCount; index++)
		{
			TypeSig rawSig = method.ParamTypes[index];
			TypeSig sig = rawSig is TypeSig.NullableSig nullable ? nullable.Inner : rawSig;
			bool optional = rawSig is TypeSig.NullableSig;
			string name = index - 1 < names.Length ? names[index - 1] : $"arg{index - 1}";

			if (IsNativeType(sig, "ctx"))
			{
				arguments.Add(KernTypeHelpers.Box(ctx));
				continue;
			}

			if (IsNativeType(sig, "session"))
			{
				arguments.Add(ctx.Session is null ? KValue.Nil : KernTypeHelpers.Box(ctx.Session));
				continue;
			}

			if (IsNativeType(sig, "claims"))
			{
				arguments.Add(ctx.Claims is null ? KValue.Nil : KernTypeHelpers.Box(ctx.Claims));
				continue;
			}

			if (sig is TypeSig.PrimSig primitive)
			{
				string? raw = ctx.Param(name) ?? ctx.Query(name);
				if (raw is null)
				{
					if (optional)
					{
						arguments.Add(KValue.Nil);
						continue;
					}

					if (primitive.Kind == PrimKind.Object)
					{
						arguments.Add(ParseBody(interpreter, sig, ctx));
						continue;
					}

					throw new BindingError($"не задан параметр '{name}'");
				}

				arguments.Add(Parse(primitive.Kind, raw, name));
				continue;
			}

			if (ctx.Body.Length == 0)
			{
				if (optional)
				{
					arguments.Add(KValue.Nil);
					continue;
				}

				throw new BindingError($"ожидалось тело запроса ({sig.Canonical})");
			}

			arguments.Add(ParseBody(interpreter, sig, ctx));
		}

		return [.. arguments];
	}

	private static KValue ParseBody(Interpreter interpreter, TypeSig sig, KernCtx ctx)
	{
		try
		{
			KType type = interpreter.Types.FromSig(sig);
			return JsonApi.ParseInto(ctx.Body, type, interpreter);
		}
		catch (Exception exception) when (exception is KernPanic or JsonException or InvalidOperationException)
		{
			throw new BindingError($"не удалось разобрать тело как {sig.Canonical}: {exception.Message}");
		}
	}

	private static KValue Parse(PrimKind kind, string raw, string name)
	{
		CultureInfo invariant = CultureInfo.InvariantCulture;
		return kind switch
		{
			PrimKind.Str => KValue.Str(raw),
			PrimKind.Bool when bool.TryParse(raw, out bool boolean) => KValue.Bool(boolean),
			PrimKind.Char when raw.Length == 1 => KValue.Char(raw[0]),
			PrimKind.F16 or PrimKind.F32 or PrimKind.F64 when
				double.TryParse(raw, NumberStyles.Float, invariant, out double number) => KValue.F64(number),
			PrimKind.I8 or PrimKind.I16 or PrimKind.I32 or PrimKind.I64 or
			PrimKind.U8 or PrimKind.U16 or PrimKind.U32 or PrimKind.U64 when
				long.TryParse(raw, NumberStyles.Integer, invariant, out long integer) => KValue.I64(integer),
			_ => throw new BindingError(
				$"параметр '{name}': '{raw}' не приводится к типу {kind.ToKernName()}"),
		};
	}

	private static KernRes Adapt(Interpreter interpreter, KValue value, TypeSig declared)
	{
		if (value.Ref is KNativeBox box && box.Value is KernRes res)
			return res;
		if (value.IsNilOrNone || value.Kind == ValueKind.Void)
			return declared is TypeSig.NullableSig
				? KernRes.Error(404, "не найдено")
				: KernRes.Make(204, "", "text/plain");
		if (value.Kind == ValueKind.Str)
			return KernRes.Make(200, value.AsStr, "text/plain; charset=utf-8");
		if (value.Ref is KObject instance &&
			instance.Type.Definition.Name is "Ok" or "Err")
		{
			KValue payload = instance.FieldCount > 0 ? instance.GetField(0) : KValue.Nil;
			if (instance.Type.Definition.Name == "Ok")
				return Adapt(interpreter, payload, declared);
			return KernRes.Error(
				400,
				payload.Kind == ValueKind.Str ? payload.AsStr : JsonApi.ToStr(payload));
		}

		return KernRes.Make(200, JsonApi.ToStr(value), "application/json; charset=utf-8");
	}

	private static string[] ParameterNames(FunctionDef method)
	{
		AttributeUse? attribute = method.Attributes.FirstOrDefault(item => item.Name == "kern.param_names");
		if (attribute is null || attribute.Args.Count == 0 || attribute.Args[0].Kind != KConstKind.Str)
			return [];
		return attribute.Args[0].AsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
	}

	private static bool IsNativeType(TypeSig sig, string name) =>
		sig is TypeSig.NamedSig named &&
		(named.FullName == name || named.FullName.EndsWith("." + name, StringComparison.Ordinal));

	private static bool HasAttribute(IEnumerable<AttributeUse> attributes, string name) =>
		attributes.Any(attribute => attribute.Name == name);

	private static string? AttributeString(IEnumerable<AttributeUse> attributes, string name) =>
		attributes.FirstOrDefault(attribute => attribute.Name == name) is { Args.Count: > 0 } attribute &&
		attribute.Args[0].Kind == KConstKind.Str
			? attribute.Args[0].AsStr
			: null;

	private static List<string> RolesOf(IEnumerable<AttributeUse> attributes, string name)
	{
		AttributeUse? attribute = attributes.FirstOrDefault(item => item.Name == name);
		if (attribute is null)
			return [];
		return [.. attribute.Args
			.Where(argument => argument.Kind == KConstKind.Str)
			.Select(argument => argument.AsStr)];
	}

	private sealed class BindingError(string message) : Exception(message);
}
