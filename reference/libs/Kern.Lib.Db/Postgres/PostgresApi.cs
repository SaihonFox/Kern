using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Lib.Db.Common;
using Kern.Vm.Values;
using Npgsql;

namespace Kern.Lib.Db.Postgres;

/// <summary>
/// PostgreSQL (драйвер Npgsql). Подключение — строкой Npgsql или URL:
/// <code>
/// postgres db("postgres://user:pass@localhost:5432/app")
/// match postgres.connect("Host=localhost;Database=app;Username=u;Password=p") { Ok(db) -> …; Err(e) -> … }
/// db.exec("insert into users(name) values (?)", ["Аня"])     // `?` → $1, $2, …
/// await db.query_async("select * from users")
/// </code>
/// Интерфейс тот же, что у sqlite: exec/query/scalar/begin/commit/rollback, плюс <c>orm.table&lt;T&gt;(db)</c>.
/// </summary>
[KernType("postgres")]
public sealed class KernPostgres : IKernDb, IDisposable
{
	private readonly DbCore _core;

	[KernCtor] public KernPostgres(string connection)
	{
		var conn = new NpgsqlConnection(ConnectionStrings.Postgres(connection));
		conn.Open();
		_core = new DbCore(conn, SqlDialect.Postgres);
	}

	[KernCtor] public KernPostgres(string host, int port, string database, string user, string password)
		: this(ConnectionStrings.Postgres(host, port, database, user, password)) { }

	/// <summary>Подключение без паники: Err с сообщением, если сервер недоступен.</summary>
	[KernStatic("connect")] public static KResult<KernPostgres, string> Connect(string connection)
	{
		try { return KResult<KernPostgres, string>.Ok(KValue.Obj(new KernPostgres(connection))); }
		catch (Exception e) when (e is NpgsqlException or InvalidOperationException or ArgumentException or System.Net.Sockets.SocketException)
		{
			return KResult<KernPostgres, string>.Err(KValue.Str(e.Message));
		}
	}

	public SqlDialect Dialect => SqlDialect.Postgres;

	[KernMember] public KResult<long, string> Exec(string sql) => _core.Exec(sql, null);
	[KernMember] public KResult<long, string> Exec(string sql, KList<object> args) => _core.Exec(sql, args);
	[KernMember] public Task<KResult<long, string>> ExecAsync(string sql) => _core.ExecAsync(sql, null);
	[KernMember] public Task<KResult<long, string>> ExecAsync(string sql, KList<object> args) => _core.ExecAsync(sql, args);

	[KernMember] public KResult<KList<KDict<string, object>>, string> Query(string sql) => _core.Query(sql, null);
	[KernMember] public KResult<KList<KDict<string, object>>, string> Query(string sql, KList<object> args) => _core.Query(sql, args);
	[KernMember] public Task<KResult<KList<KDict<string, object>>, string>> QueryAsync(string sql) => _core.QueryAsync(sql, null);
	[KernMember] public Task<KResult<KList<KDict<string, object>>, string>> QueryAsync(string sql, KList<object> args) => _core.QueryAsync(sql, args);

	[KernMember] public KResult<object?, string> Scalar(string sql, KList<object> args) => _core.Scalar(sql, args);

	/// <summary>id последней вставки в serial-колонку этого соединения (`lastval()`).</summary>
	[KernProperty] public long LastId => _core.LastId;

	[KernMember] public void Begin() => _core.Begin();
	[KernMember] public void Commit() => _core.Commit();
	[KernMember] public void Rollback() => _core.Rollback();
	[KernMember] public void Close() => Dispose();

	public void Dispose() => _core.Dispose();
}
