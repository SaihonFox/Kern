using System.Text;
using Kern.Binding.Attributes;
using Kern.Lib.Backend.Http;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Backend.Middleware;

/// <summary>
/// Готовый middleware: <c>a.use(middleware.logger())</c>. Свой middleware пишется на Kern:
/// <c>a.use(fun(ctx c, chain n) -> res { n.next() })</c>.
/// </summary>
[KernType("middleware")]
public sealed class KernMiddleware
{
	internal Layer Layer { get; }

	internal KernMiddleware(Layer layer) => Layer = layer;

	internal static KernMiddleware Require(KNativeBox self) => self.Value switch
	{
		KernMiddleware middleware => middleware,
		_ => throw new KernPanic($"middleware: ожидался middleware, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Встроенные middleware (spec round 21).</summary>
[KernType("middleware")]
public static class MiddlewareApi
{
	/// <summary>Логирует «GET /path 200 3ms».</summary>
	[KernStatic]
	public static KernMiddleware Logger() => new((ctx, next) =>
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();
		KernRes res = next.Next();
		Console.Out.WriteLine($"{ctx.Method} {ctx.Path} {res.Status} {watch.ElapsedMilliseconds}ms");
		return res;
	});

	/// <summary>Ловит panic обработчика и отдаёт 500 JSON-ошибкой.</summary>
	[KernStatic]
	public static KernMiddleware Recover() => new((ctx, next) =>
	{
		try
		{
			return next.Next();
		}
		catch (KernPanic panic)
		{
			return KernRes.Error(500, "ошибка сервера: " + panic.Message);
		}
	});

	/// <summary>CORS с origin <c>*</c>; preflight (OPTIONS) → 204.</summary>
	[KernStatic]
	public static KernMiddleware Cors() => Cors("*");

	/// <summary>CORS с указанным origin; preflight (OPTIONS) → 204.</summary>
	[KernStatic]
	public static KernMiddleware Cors(string origin) => new((ctx, next) =>
	{
		KernRes res = ctx.Method == "OPTIONS" ? KernRes.Make(204, "", "text/plain") : next.Next();
		res.WithHeader("Access-Control-Allow-Origin", origin)
			.WithHeader("Access-Control-Allow-Methods", "GET, POST, PUT, PATCH, DELETE, OPTIONS")
			.WithHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");
		return res;
	});

	/// <summary><c>Authorization: Bearer &lt;token&gt;</c>; при совпадении <c>ctx.user = "bearer"</c>.</summary>
	[KernStatic]
	public static KernMiddleware Bearer(string token) => new((ctx, next) =>
	{
		if (BearerOf(ctx) != token)
			return KernRes.Error(401, "требуется авторизация").WithHeader("WWW-Authenticate", "Bearer");
		ctx.User ??= "bearer";
		return next.Next();
	});

	/// <summary>HTTP Basic; при успехе <c>ctx.user = имя</c>.</summary>
	[KernStatic]
	public static KernMiddleware Basic(string user, string password) => new((ctx, next) =>
	{
		string? header = ctx.Header("Authorization");
		if (header is not null && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				string[] pair = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..])).Split(':', 2);
				if (pair.Length == 2 && pair[0] == user && pair[1] == password)
				{
					ctx.User = user;
					return next.Next();
				}
			}
			catch (FormatException)
			{
				// Некорректный base64 — как отсутствие заголовка.
			}
		}

		return KernRes.Error(401, "требуется авторизация").WithHeader("WWW-Authenticate", "Basic realm=\"kern\"");
	});

	/// <summary>Проверяет JWT из Bearer; задаёт <c>ctx.user</c>/<c>ctx.roles</c>; без токена — 401.</summary>
	[KernStatic]
	public static KernMiddleware JwtAuth(string secret) => JwtLayer(secret, required: true);

	/// <summary>Как <c>jwt_auth</c>, но без токена пропускает запрос.</summary>
	[KernStatic]
	public static KernMiddleware JwtOptional(string secret) => JwtLayer(secret, required: false);

	private static KernMiddleware JwtLayer(string secret, bool required) => new((ctx, next) =>
	{
		string? token = BearerOf(ctx);
		if (token is null)
			return required ? KernRes.Error(401, "требуется авторизация") : next.Next();
		(KernClaims? claims, string? error) = Jwt.Verify(token, secret);
		if (claims is null)
			return KernRes.Error(401, error ?? "неверный токен");
		ctx.User = claims.Sub;
		ctx.RoleList = claims.RoleList;
		ctx.Claims = claims;
		return next.Next();
	});

	/// <summary>Требует роль (после jwt/basic auth): нет пользователя — 401, нет роли — 403.</summary>
	[KernStatic]
	public static KernMiddleware RequireRole(string role) => new((ctx, next) =>
	{
		if (ctx.User is null)
			return KernRes.Error(401, "требуется авторизация");
		return ctx.HasRole(role) ? next.Next() : KernRes.Error(403, "доступ запрещён");
	});

	/// <summary>Сессии в памяти: cookie <c>kern_sid</c>, TTL 30 минут.</summary>
	[KernStatic]
	public static KernMiddleware Sessions() => Sessions(null);

	/// <summary>Сессии с сохранением в JSON-файл (<paramref name="path"/>); nil — только память.</summary>
	[KernStatic]
	public static KernMiddleware Sessions(string? path)
	{
		var store = new SessionStore(TimeSpan.FromMinutes(30), path);
		return new KernMiddleware((ctx, next) =>
		{
			KernSession session = store.GetOrCreate(ctx.Cookie("kern_sid"), out bool created);
			ctx.Session = session;
			KernRes res = next.Next();
			if (created)
				res.Cookie("kern_sid", session.Id);
			store.Save();
			return res;
		});
	}

	/// <summary>Не больше <paramref name="max"/> запросов с IP за <paramref name="seconds"/> секунд; иначе 429.</summary>
	[KernStatic]
	public static KernMiddleware RateLimit(int max, int seconds)
	{
		var hits = new Dictionary<string, Queue<DateTime>>();
		return new KernMiddleware((ctx, next) =>
		{
			DateTime now = DateTime.UtcNow;
			lock (hits)
			{
				if (!hits.TryGetValue(ctx.Ip, out Queue<DateTime>? queue))
					hits[ctx.Ip] = queue = new Queue<DateTime>();
				while (queue.Count > 0 && (now - queue.Peek()).TotalSeconds > seconds)
					queue.Dequeue();
				if (queue.Count >= max)
					return KernRes.Error(429, "слишком много запросов")
						.WithHeader("Retry-After", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
				queue.Enqueue(now);
			}

			return next.Next();
		});
	}

	internal static string? BearerOf(KernCtx ctx)
	{
		string? header = ctx.Header("Authorization");
		return header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
			? header[7..].Trim()
			: null;
	}
}
