using System.Text;
using Kern.Binding.Attributes;
using Kern.Bytecode;
using Kern.Vm;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Http;

/// <summary>
/// Прокси типизированного HTTP-клиента (spec round 10, вариант B): абстрактные методы с
/// атрибутами <c>#[get("/path/{id}")]</c>/<c>#[post]</c>/… выполняют реальный запрос.
/// </summary>
public sealed class HttpProxy(string baseUrl, IReadOnlyDictionary<string, string>? headers = null) : IKProxyHandler
{
	private static readonly string[] Verbs = ["get", "post", "put", "patch", "delete"];

	public KValue Invoke(Interpreter interpreter, KObject receiver, FunctionDef method, KValue[] arguments)
	{
		AttributeUse? verb = method.Attributes.FirstOrDefault(attribute => Verbs.Contains(attribute.Name));
		if (verb is null)
			throw new KernPanic($"http_client.proxy: у метода '{method.Name}' нет атрибута #[get/post/put/patch/delete]");

		string path = verb.Args.Count > 0 ? verb.Args[0].AsStr : "/";
		string[] parameterNames = ParameterNames(method);
		string NameAt(int argumentIndex) =>
			argumentIndex >= 1 && argumentIndex - 1 < parameterNames.Length
				? parameterNames[argumentIndex - 1]
				: $"arg{argumentIndex - 1}";

		var used = new bool[method.ParamTypes.Length];
		string url = Substitute(path, NameAt, arguments, used);
		var query = new StringBuilder();
		string? body = null;
		for (int index = 1; index < method.ParamTypes.Length && index < arguments.Length; index++)
		{
			if (used[index])
				continue;
			KValue value = arguments[index];
			if (verb.Name is "post" or "put" or "patch")
			{
				body ??= value.Kind == ValueKind.Str ? value.AsStr : Json.JsonApi.Serialize(value);
				continue;
			}

			query.Append(query.Length == 0 ? '?' : '&');
			query.Append(Uri.EscapeDataString(NameAt(index)));
			query.Append('=');
			query.Append(Uri.EscapeDataString(value.Kind == ValueKind.Str ? value.AsStr : Kern.Binding.Formatting.KernFormat.Value(value)));
		}

		string absolute = Absolute(url) + query;
		KType? target = ResultValue(interpreter.Types.FromSig(method.ReturnType));
		return HttpApi.Start(
			interpreter,
			verb.Name.ToUpperInvariant(),
			absolute,
			body,
			headers,
			target);
	}

	private static string Substitute(string path, Func<int, string> nameAt, KValue[] arguments, bool[] used)
	{
		var result = new StringBuilder(path);
		for (int index = 1; index < arguments.Length; index++)
		{
			string placeholder = "{" + nameAt(index) + "}";
			string text = result.ToString();
			if (!text.Contains(placeholder, StringComparison.Ordinal))
				continue;
			KValue value = arguments[index];
			string rendered = value.Kind == ValueKind.Str
				? value.AsStr
				: Kern.Binding.Formatting.KernFormat.Value(value);
			result.Replace(placeholder, Uri.EscapeDataString(rendered));
			used[index] = true;
		}

		return result.ToString();
	}

	private static string[] ParameterNames(FunctionDef method)
	{
		foreach (AttributeUse attribute in method.Attributes)
		{
			if (attribute.Name != "kern.param_names" || attribute.Args.Count == 0)
				continue;
			return attribute.Args[0].AsStr.Split(',', StringSplitOptions.TrimEntries);
		}

		return [];
	}

	/// <summary>Тип значения из <c>result&lt;T, E&gt;</c> (union Ok&lt;T&gt;/Err&lt;E&gt;).</summary>
	private static KType? ResultValue(KType type)
	{
		if (type is KBuiltinGenericType { Builtin: BuiltinGenericKind.Task } task && task.TypeArgs.Length > 0)
			type = task.TypeArgs[0];

		if (type is KUnionType union)
		{
			foreach (KType member in union.Members)
				if (member is KClassType { Definition.Name: "Ok" } ok && ok.TypeArgs.Length > 0)
					return ok.TypeArgs[0];
		}

		if (type is KClassType { Definition.Name: "Ok" } direct && direct.TypeArgs.Length > 0)
			return direct.TypeArgs[0];
		return null;
	}

	private string Absolute(string path) =>
		path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
		path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
			? path
			: baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
}
