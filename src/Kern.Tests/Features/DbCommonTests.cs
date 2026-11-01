using Kern.Lib.Db.Common;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>Общая часть БД: URL/DSN, диалекты SQL и преобразование значений.</summary>
public sealed class DbCommonTests
{
	[Fact]
	public void Postgres_url_becomes_a_connection_string()
	{
		string text = ConnectionStrings.Postgres("postgres://user:p%40ss@db.local:5544/my%20db");

		Assert.Contains("Host=db.local", text);
		Assert.Contains("Port=5544", text);
		Assert.Contains("Database=my db", text);
		Assert.Contains("Username=user", text);
		Assert.Contains("Password=p@ss", text);
	}

	[Fact]
	public void Mysql_url_becomes_a_connection_string()
	{
		string text = ConnectionStrings.Mysql("mysql://root:secret@127.0.0.1:3307/app");

		Assert.Contains("Server=127.0.0.1", text);
		Assert.Contains("Port=3307", text);
		Assert.Contains("Database=app", text);
		Assert.Contains("User ID=root", text);
		Assert.Contains("Password=secret", text);
	}

	[Fact]
	public void Plain_connection_strings_are_kept()
	{
		const string pg = "Host=localhost;Database=app;Username=u;Password=p";
		const string mysql = "Server=localhost;Database=app;User ID=u;Password=p";

		Assert.Equal(pg, ConnectionStrings.Postgres(pg));
		Assert.Equal(mysql, ConnectionStrings.Mysql(mysql));
	}

	[Fact]
	public void Postgres_placeholders_skip_strings_identifiers_and_comments()
	{
		const string sql = "select '?' as s, \"a?b\", ? + ? -- ?\n/* ? */";
		string rewritten = SqlDialect.Postgres.Rewrite(sql);

		Assert.Contains("'?' as s", rewritten);
		Assert.Contains("\"a?b\"", rewritten);
		Assert.Contains("@p1 + @p2", rewritten);
		Assert.Contains("-- ?", rewritten);
		Assert.Contains("/* ? */", rewritten);
	}

	[Fact]
	public void Mysql_keeps_question_mark_placeholders()
	{
		const string sql = "select * from users where id = ? and name = ?";

		Assert.Equal(sql, SqlDialect.Mysql.Rewrite(sql));
	}

	[Fact]
	public void Dialect_quotes_identifiers()
	{
		Assert.Equal("\"users\"", SqlDialect.Postgres.Quote("users"));
		Assert.Equal("`users`", SqlDialect.Mysql.Quote("users"));
		Assert.Equal("\"a\"\"b\"", SqlDialect.Sqlite.Quote("a\"b"));
	}

	[Fact]
	public void Dialects_use_boolean_columns_for_bool_fields()
	{
		Assert.Equal("BOOLEAN", SqlDialect.Postgres.BoolType);
		Assert.Equal("BOOLEAN", SqlDialect.Mysql.BoolType);
		Assert.Equal("INTEGER", SqlDialect.Sqlite.BoolType);
	}

	[Fact]
	public void Values_round_trip_through_db_objects()
	{
		Assert.Equal(KValue.Str("x"), DbCore.FromDb("x"));
		Assert.Equal(KValue.Bool(true), DbCore.FromDb(true));
		Assert.Equal(KValue.I64(42), DbCore.FromDb(42L));
		Assert.Equal(KValue.F64(1.5), DbCore.FromDb(1.5));
		Assert.Same(DBNull.Value, DbCore.ToDb(KValue.Nil));
		Assert.True((bool)DbCore.ToDb(KValue.Bool(true)));
		Assert.Equal(42L, (long)DbCore.ToDb(KValue.I32(42)));
		Assert.Equal("x", (string)DbCore.ToDb(KValue.Str("x")));
	}
}
