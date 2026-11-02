using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kern.Lib.Backend.Routing;

namespace Kern.Lib.Backend.OpenApi;

/// <summary>
/// Построение OpenAPI 3.1 JSON по зарегистрированным HTTP-маршрутам.
/// WebSocket/SSE пропускаются; path-параметры описываются как обязательные строки.
/// </summary>
internal static class OpenApiDocument
{
	/// <summary>Собрать документ OpenAPI 3.1.0 (строка JSON).</summary>
	public static string Build(IReadOnlyList<RouteEntry> routes, string title, string version)
	{
		var paths = new JsonObject();
		foreach (IGrouping<string, RouteEntry> group in routes
			.Where(route => route.Kind == RouteKind.Http)
			.GroupBy(route => route.Template, StringComparer.Ordinal)
			.OrderBy(group => group.Key, StringComparer.Ordinal))
		{
			var operations = new JsonObject();
			foreach (RouteEntry route in group.OrderBy(route => route.Method, StringComparer.Ordinal))
			{
				string method = route.Method.ToLowerInvariant();
				if (operations.ContainsKey(method))
					continue;
				operations[method] = Operation(route, method);
			}

			if (operations.Count > 0)
				paths[group.Key] = operations;
		}

		var document = new JsonObject
		{
			["openapi"] = "3.1.0",
			["info"] = new JsonObject
			{
				["title"] = title,
				["version"] = version,
			},
			["paths"] = paths,
			["components"] = new JsonObject(),
		};
		return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
	}

	private static JsonObject Operation(RouteEntry route, string method)
	{
		var operation = new JsonObject
		{
			["operationId"] = Sanitize(method + "_" + route.Template),
			["summary"] = route.Method + " " + route.Template,
			["tags"] = new JsonArray(Tag(route.Template)),
			["parameters"] = Parameters(route.Pattern),
			["responses"] = new JsonObject
			{
				["200"] = new JsonObject { ["description"] = "OK" },
			},
		};
		return operation;
	}

	private static JsonArray Parameters(RoutePattern pattern)
	{
		var parameters = new JsonArray();
		foreach ((string name, _) in pattern.Parameters)
		{
			parameters.Add(new JsonObject
			{
				["name"] = name,
				["in"] = "path",
				["required"] = true,
				["schema"] = new JsonObject { ["type"] = "string" },
			});
		}

		return parameters;
	}

	/// <summary>Первый статический сегмент пути или <c>api</c>.</summary>
	private static string Tag(string template)
	{
		foreach (string segment in template.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			if (!segment.StartsWith('{'))
				return segment;
		}

		return "api";
	}

	/// <summary>Оставить в <c>operationId</c> только <c>[A-Za-z0-9_]</c>.</summary>
	private static string Sanitize(string text)
	{
		var builder = new StringBuilder(text.Length);
		foreach (char symbol in text)
		{
			if (char.IsAsciiLetterOrDigit(symbol) || symbol == '_')
				builder.Append(symbol);
			else
				builder.Append('_');
		}

		return builder.ToString();
	}
}
