using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Lib.Db.Common;
using Microsoft.Data.Sqlite;

namespace Kern.Lib.Db.Sqlite;

/// <summary>
/// SQLite: <c>sqlite db("app.db")</c> (":memory:" — в памяти), <c>db.exec("insert …", [1, "a"])</c>, <c>db.query("select …")</c>.
/// Параметры подставляются по порядку вместо `?`. Ошибки SQL возвращаются как Err. Async-варианты: <c>exec_async</c>, <c>query_async</c>.
/// </summary>
[KernType("sqlite")]
public sealed class KernSqlite : IKernDb, IDisposable
{
	private readonly DbCore _core;

	// Явная инициализация провайдера SQLitePCLRaw (e_sqlite3): библиотека загружается из папки libs, а не как зависимость приложения,
	// поэтому автопоиск провайдера по имени сборки может не сработать. Нативный e_sqlite3 ищет LibraryLoader (runtimes/<rid>/native).
	static KernSqlite() => SQLitePCL.Batteries_V2.Init();

	[KernCtor] public KernSqlite(string path)
	{
		var conn = new SqliteConnection(path == ":memory:" ? "Data Source=:memory:" : $"Data Source={path}");
		conn.Open();
		_core = new DbCore(conn, SqlDialect.Sqlite);
	}

	public SqlDialect Dialect => SqlDialect.Sqlite;

	/// <summary>INSERT/UPDATE/DELETE/DDL; результат — число затронутых строк.</summary>
	[KernMember] public KResult<long, string> Exec(string sql) => _core.Exec(sql, null);
	[KernMember] public KResult<long, string> Exec(string sql, KList<object> args) => _core.Exec(sql, args);
	[KernMember] public Task<KResult<long, string>> ExecAsync(string sql) => _core.ExecAsync(sql, null);
	[KernMember] public Task<KResult<long, string>> ExecAsync(string sql, KList<object> args) => _core.ExecAsync(sql, args);

	/// <summary>SELECT: список строк, каждая — dict&lt;str, object&gt; (колонка → значение).</summary>
	[KernMember] public KResult<KList<KDict<string, object>>, string> Query(string sql) => _core.Query(sql, null);
	[KernMember] public KResult<KList<KDict<string, object>>, string> Query(string sql, KList<object> args) => _core.Query(sql, args);
	[KernMember] public Task<KResult<KList<KDict<string, object>>, string>> QueryAsync(string sql) => _core.QueryAsync(sql, null);
	[KernMember] public Task<KResult<KList<KDict<string, object>>, string>> QueryAsync(string sql, KList<object> args) => _core.QueryAsync(sql, args);

	/// <summary>Первое значение первой строки или nil.</summary>
	[KernMember] public KResult<object?, string> Scalar(string sql, KList<object> args) => _core.Scalar(sql, args);

	[KernProperty] public long LastId => _core.LastId;

	[KernMember] public void Begin() => _core.Begin();
	[KernMember] public void Commit() => _core.Commit();
	[KernMember] public void Rollback() => _core.Rollback();
	[KernMember] public void Close() => Dispose();

	public void Dispose() => _core.Dispose();
}
