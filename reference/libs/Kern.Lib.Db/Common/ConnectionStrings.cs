namespace Kern.Lib.Db.Common;

/// <summary>Приводит URL вида `postgres://user:pass@host:5432/db` к строке подключения драйвера; обычные строки возвращает как есть.</summary>
public static class ConnectionStrings
{
	public static string Postgres(string text)
	{
		if (!TryUrl(text, ["postgres", "postgresql"], out var u)) return text;
		var (user, password) = Credentials(u);
		return $"Host={u.Host};Port={(u.Port > 0 ? u.Port : 5432)};Database={u.AbsolutePath.Trim('/')};Username={user};Password={password}";
	}

	public static string Mysql(string text)
	{
		if (!TryUrl(text, ["mysql", "mariadb"], out var u)) return text;
		var (user, password) = Credentials(u);
		return $"Server={u.Host};Port={(u.Port > 0 ? u.Port : 3306)};Database={u.AbsolutePath.Trim('/')};User ID={user};Password={password}";
	}

	public static string Postgres(string host, int port, string database, string user, string password) =>
		$"Host={host};Port={port};Database={database};Username={user};Password={password}";

	public static string Mysql(string host, int port, string database, string user, string password) =>
		$"Server={host};Port={port};Database={database};User ID={user};Password={password}";

	private static bool TryUrl(string text, string[] schemes, out Uri uri)
	{
		uri = null!;
		return Uri.TryCreate(text, UriKind.Absolute, out var u) && schemes.Contains(u.Scheme) && (uri = u) is not null;
	}

	private static (string User, string Password) Credentials(Uri u)
	{
		var parts = u.UserInfo.Split(':', 2);
		return (Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
	}
}
