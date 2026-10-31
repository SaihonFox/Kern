using System.Text;
using Kern.Binding.Attributes;
using Kern.Lib.Backend.Http;
using Kern.Vm.Execution;

namespace Kern.Lib.Backend.Middleware;

/// <summary>
/// Готовые middleware: `a.use(middleware.logger())`. Свои пишутся на Kern: `a.use(fun(ctx c, chain n) -> res { … n.next() })`.
/// </summary>
[KernType("middleware")]
public sealed class KernMiddleware
{
	internal Layer Layer { get; }
	internal KernMiddleware(Layer layer) => Layer = layer;

	/// <summary>Печатает «GET /path 200 3ms».</summary>
	[KernStatic]
	public static KernMiddleware Logger() => new((ctx, next) =>
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var res = next.Next();
		Console.Out.WriteLine($"{ctx.Method} {ctx.Path} {res.Status} {sw.ElapsedMilliseconds}ms");
		return res;
	});

	/// <summary>Ловит panic внутри обработчиков и отвечает 500 JSON-ошибкой.</summary>
	[KernStatic]
	public static KernMiddleware Recover() => new((ctx, next) =>
	{
		try { return next.Next(); }
		catch (KernPanic p) { return KernRes.Error(500, "ошибка сервера: " + p.Message); }
	});

	/// <summary>CORS: заголовки Access-Control-*; preflight (OPTIONS) отвечает 204.</summary>
	[KernStatic]
	public static KernMiddleware Cors(string origin = "*") => new((ctx, next) =>
	{
		var res = ctx.Method == "OPTIONS" ? KernRes.NoContent() : next.Next();
		res.Header("Access-Control-Allow-Origin", origin)
			.Header("Access-Control-Allow-Methods", "GET, POST, PUT, PATCH, DELETE, OPTIONS")
			.Header("Access-Control-Allow-Headers", "Content-Type, Authorization");
		return res;
	});

	/// <summary>`Authorization: Bearer &lt;token&gt;` должен совпасть с токеном; пользователь — "bearer".</summary>
	[KernStatic]
	public static KernMiddleware Bearer(string token) => new((ctx, next) =>
	{
		if (BearerOf(ctx) != token) return KernRes.Unauthorized().Header("WWW-Authenticate", "Bearer");
		ctx.User ??= "bearer";
		return next.Next();
	});

	/// <summary>HTTP Basic: проверяет логин и пароль, ctx.user = логин.</summary>
	[KernStatic]
	public static KernMiddleware Basic(string user, string password) => new((ctx, next) =>
	{
		var h = ctx.Header("Authorization");
		if (h is not null && h.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				var pair = Encoding.UTF8.GetString(Convert.FromBase64String(h[6..])).Split(':', 2);
				if (pair.Length == 2 && pair[0] == user && pair[1] == password)
				{
					ctx.User = user;
					return next.Next();
				}
			}
			catch (FormatException) { }
		}
		return KernRes.Unauthorized().Header("WWW-Authenticate", "Basic realm=\"kern\"");
	});

	/// <summary>Проверяет JWT из заголовка Bearer; ставит ctx.user и ctx.roles; без токена — 401.</summary>
	[KernStatic] public static KernMiddleware JwtAuth(string secret) => JwtLayer(secret, required: true);

	/// <summary>То же, но запросы без токена проходят анонимно (проверяйте ctx.user).</summary>
	[KernStatic] public static KernMiddleware JwtOptional(string secret) => JwtLayer(secret, required: false);

	private static KernMiddleware JwtLayer(string secret, bool required) => new((ctx, next) =>
	{
		var token = BearerOf(ctx);
		if (token is null)
			return required ? KernRes.Unauthorized() : next.Next();
		var (claims, error) = Jwt.Verify(token, secret);
		if (claims is null) return KernRes.Unauthorized(error!);
		ctx.User = claims.Sub;
		ctx.RoleList = claims.RoleList;
		return next.Next();
	});

	/// <summary>Требует роль (после jwt/свой auth): иначе 403; анонимам — 401.</summary>
	[KernStatic]
	public static KernMiddleware RequireRole(string role) => new((ctx, next) =>
	{
		if (ctx.User is null) return KernRes.Unauthorized();
		return ctx.HasRole(role) ? next.Next() : KernRes.Forbidden();
	});

	/// <summary>Сессии в памяти: cookie `kern_sid`, ctx.session; срок — 30 минут бездействия.</summary>
	[KernStatic]
	public static KernMiddleware Sessions()
	{
		var store = new SessionStore(TimeSpan.FromMinutes(30));
		return new((ctx, next) =>
		{
			var session = store.GetOrCreate(ctx.Cookie("kern_sid"), out bool created);
			ctx.Session = session;
			var res = next.Next();
			if (created) res.Cookie("kern_sid", session.Id);
			return res;
		});
	}

	/// <summary>Не больше <paramref name="max"/> запросов с одного IP за <paramref name="seconds"/> секунд; иначе 429.</summary>
	[KernStatic]
	public static KernMiddleware RateLimit(int max, int seconds)
	{
		var hits = new Dictionary<string, Queue<DateTime>>();
		return new((ctx, next) =>
		{
			var now = DateTime.UtcNow;
			lock (hits)
			{
				if (!hits.TryGetValue(ctx.Ip, out var q)) hits[ctx.Ip] = q = new Queue<DateTime>();
				while (q.Count > 0 && (now - q.Peek()).TotalSeconds > seconds) q.Dequeue();
				if (q.Count >= max) return KernRes.Error(429, "слишком много запросов").Header("Retry-After", seconds.ToString());
				q.Enqueue(now);
			}
			return next.Next();
		});
	}

	internal static string? BearerOf(KernCtx ctx)
	{
		var h = ctx.Header("Authorization");
		return h is not null && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
	}
}
