using System.Text.Json;
using Kern.Binding.Attributes;
using Kern.Lib.Backend.Http;
using Kern.Lib.Backend.OpenApi;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Routing;

/// <summary>OpenAPI: документ по маршрутам, HTTP-маршрут документа и HTML-страница.</summary>
public sealed partial class KernApp
{
	private const string DefaultOpenApiTitle = "Kern API";
	private const string DefaultOpenApiVersion = "1.0.0";
	private readonly HashSet<RouteEntry> _openApiRoutes = [];

	internal const string OpenApiTitle = DefaultOpenApiTitle;
	internal const string OpenApiVersion = DefaultOpenApiVersion;

	internal void MarkOpenApiRoute(RouteEntry route) => _openApiRoutes.Add(route);

	/// <summary>
	/// Собрать документ по текущим маршрутам. Маршруты самого OpenAPI (зарегистрированные
	/// через <c>openapi</c>) в документ не попадают — иначе он ссылался бы на себя.
	/// </summary>
	internal string OpenApiJson(string title, string version)
	{
		RouteEntry[] routes = [.. Routes.Where(route => !_openApiRoutes.Contains(route))];
		return OpenApiDocument.Build(routes, title, version);
	}
}

/// <summary>Члены типа <c>app</c> для OpenAPI.</summary>
[KernType("app", Parent = "route_group")]
public static class AppOpenApiApi
{
	/// <summary><c>a.openapi(path)</c>: отдаёт OpenAPI 3.1 JSON по HTTP-маршрутам.</summary>
	[KernMember]
	[KernName("openapi")]
	public static void OpenApi(KNativeBox self, string path) =>
		OpenApi(self, path, KernApp.OpenApiTitle, KernApp.OpenApiVersion);

	/// <summary><c>a.openapi(path, title, version)</c>: то же с заголовком и версией.</summary>
	[KernMember]
	[KernName("openapi")]
	public static void OpenApi(KNativeBox self, string path, string title, string version)
	{
		KernApp app = KernApp.Unwrap(self);
		string template = RoutePattern.Join(app.FullPrefix, path);
		RouteEntry route = app.AddHttp("GET", template, _ => KernRes.Make(
			200,
			app.OpenApiJson(title, version),
			"application/json; charset=utf-8"));
		app.MarkOpenApiRoute(route);
	}

	/// <summary><c>a.openapi_document()</c>: документ OpenAPI строкой.</summary>
	[KernMember]
	[KernName("openapi_document")]
	public static string Document(KNativeBox self) =>
		Document(self, KernApp.OpenApiTitle, KernApp.OpenApiVersion);

	/// <summary><c>a.openapi_document(title, version)</c>: документ OpenAPI строкой.</summary>
	[KernMember]
	[KernName("openapi_document")]
	public static string Document(KNativeBox self, string title, string version) =>
		KernApp.Unwrap(self).OpenApiJson(title, version);

	/// <summary><c>a.openapi_ui(path)</c>: самодостаточная HTML-страница документа.</summary>
	[KernMember]
	[KernName("openapi_ui")]
	public static void Ui(KNativeBox self, string path) => Ui(self, path, "/openapi.json");

	/// <summary><c>a.openapi_ui(path, json_path)</c>: HTML-страница, читающая документ по URL.</summary>
	[KernMember]
	[KernName("openapi_ui")]
	public static void Ui(KNativeBox self, string path, string jsonPath)
	{
		if (jsonPath.Length == 0)
			throw new KernPanic("app.openapi_ui: json_path не может быть пустым");
		KernApp app = KernApp.Unwrap(self);
		string template = RoutePattern.Join(app.FullPrefix, path);
		app.AddHttp("GET", template, _ => KernRes.Make(200, Page(jsonPath), "text/html; charset=utf-8"));
	}

	private static string Page(string jsonPath)
	{
		string title = System.Net.WebUtility.HtmlEncode("Kern API");
		string scriptPath = JsonSerializer.Serialize(jsonPath)
			.Replace("<", "\\u003C", StringComparison.Ordinal);
		return $$"""
			<!doctype html>
			<html lang="ru">
			<head>
			<meta charset="utf-8">
			<meta name="viewport" content="width=device-width, initial-scale=1">
			<title>{{title}}</title>
			<style>
			body { font-family: system-ui, sans-serif; margin: 2rem; color: #1f2933; background: #f8fafc; }
			h1 { font-size: 1.4rem; }
			table { border-collapse: collapse; width: 100%; background: #fff; }
			th, td { border: 1px solid #d9e2ec; padding: .45rem .6rem; text-align: left; vertical-align: top; }
			th { background: #eef2f7; }
			td.method { font-weight: 600; white-space: nowrap; }
			#status { margin: .8rem 0; color: #52606d; }
			</style>
			</head>
			<body>
			<h1>{{title}}</h1>
			<div id="status">загрузка…</div>
			<table id="ops">
			<thead><tr><th>Метод</th><th>Путь</th><th>Описание</th></tr></thead>
			<tbody></tbody>
			</table>
			<script>
			const jsonPath = {{scriptPath}};
			const status = document.getElementById('status');
			const rows = document.querySelector('#ops tbody');
			fetch(jsonPath).then((response) => response.json()).then((doc) => {
				const paths = doc.paths || {};
				let count = 0;
				Object.keys(paths).sort().forEach((path) => {
					Object.keys(paths[path]).sort().forEach((method) => {
						const operation = paths[path][method] || {};
						const row = document.createElement('tr');
						const methodCell = document.createElement('td');
						methodCell.className = 'method';
						methodCell.textContent = method.toUpperCase();
						const pathCell = document.createElement('td');
						pathCell.textContent = path;
						const summaryCell = document.createElement('td');
						summaryCell.textContent = operation.summary || '';
						row.appendChild(methodCell);
						row.appendChild(pathCell);
						row.appendChild(summaryCell);
						rows.appendChild(row);
						count = count + 1;
					});
				});
				status.textContent = count === 0 ? 'нет маршрутов' : 'операций: ' + count;
			}).catch(() => {
				status.textContent = 'нет маршрутов (не удалось загрузить ' + jsonPath + ')';
			});
			</script>
			</body>
			</html>
			""";
	}
}
