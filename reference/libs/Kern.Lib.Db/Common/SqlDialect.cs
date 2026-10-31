namespace Kern.Lib.Db.Common;

/// <summary>Различия SQL-диалектов, нужные драйверам и мини-ORM: кавычки, типы колонок, автонумерация, плейсхолдеры.</summary>
public sealed class SqlDialect
{
	public required string Name { get; init; }
	public required char QuoteOpen { get; init; }
	public required char QuoteClose { get; init; }
	public required string IntegerType { get; init; }
	public required string RealType { get; init; }
	public required string TextType { get; init; }
	/// <summary>Фрагмент колонки-ключа с автонумерацией (после имени): `INTEGER PRIMARY KEY AUTOINCREMENT`.</summary>
	public required string AutoKey { get; init; }
	/// <summary>Как записать n-й (с нуля) параметр; null — оставить `?` как есть (драйвер понимает позиционные `?`).</summary>
	public Func<int, string>? Placeholder { get; init; }
	/// <summary>Имя ADO.NET-параметра для n-го значения; null — безымянный (позиционный).</summary>
	public Func<int, string>? ParameterName { get; init; }
	/// <summary>SQL, возвращающий id последней вставленной строки в этом соединении.</summary>
	public required string LastIdSql { get; init; }

	public string Quote(string name) => QuoteOpen + name.Replace(QuoteClose.ToString(), new string(QuoteClose, 2)) + QuoteClose;

	/// <summary>Заменяет `?` (вне строковых литералов) на плейсхолдеры диалекта.</summary>
	public string Rewrite(string sql)
	{
		if (Placeholder is null) return sql;
		var sb = new System.Text.StringBuilder(sql.Length + 8);
		bool quoted = false;
		int n = 0;
		foreach (char c in sql)
		{
			if (c == '\'') quoted = !quoted;
			if (c == '?' && !quoted) sb.Append(Placeholder(n++)); else sb.Append(c);
		}
		return sb.ToString();
	}

	public static readonly SqlDialect Sqlite = new()
	{
		Name = "sqlite", QuoteOpen = '"', QuoteClose = '"',
		IntegerType = "INTEGER", RealType = "REAL", TextType = "TEXT",
		AutoKey = "INTEGER PRIMARY KEY AUTOINCREMENT",
		Placeholder = i => "$" + i, ParameterName = i => "$" + i,
		LastIdSql = "select last_insert_rowid()",
	};

	public static readonly SqlDialect Postgres = new()
	{
		Name = "postgres", QuoteOpen = '"', QuoteClose = '"',
		IntegerType = "BIGINT", RealType = "DOUBLE PRECISION", TextType = "TEXT",
		AutoKey = "BIGSERIAL PRIMARY KEY",
		Placeholder = i => "$" + (i + 1),
		LastIdSql = "select lastval()",
	};

	public static readonly SqlDialect Mysql = new()
	{
		Name = "mysql", QuoteOpen = '`', QuoteClose = '`',
		IntegerType = "BIGINT", RealType = "DOUBLE", TextType = "TEXT",
		AutoKey = "BIGINT PRIMARY KEY AUTO_INCREMENT",
		LastIdSql = "select last_insert_id()",
	};
}
