using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Lib.Db.Common;
using Kern.Vm.Values;
using MySqlConnector;

namespace Kern.Lib.Db.Mysql;

/// <summary>
/// MySQL / MariaDB (драйвер MySqlConnector). Подключение — строкой драйвера или URL:
/// <code>
/// mysql db("mysql://user:pass@localhost:3306/app")
/// match mysql.connect("Server=localhost;Database=app;User ID=u;Password=p") { Ok(db) -> …; Err(e) -> … }
/// db.exec("insert into users(name) values (?)", ["Аня"])
/// await db.query_async("select * from users")
/// </code>
/// Интерфейс тот же, что у sqlite и postgres; работает с <c>orm.table&lt;T&gt;(db)</c>.
/// </summary>
[KernType("mysql")]
public sealed class KernMysql : IKernDb, IDisposable
{
	private readonly DbCore _core;

	[KernCtor] public KernMysql(string connection)
	{
		var conn = new MySqlConnection(ConnectionStrings.Mysql(connection));
		conn.Open();
		_core = new DbCore(conn, SqlDialect.Mysql);
	}

	[KernCtor] public KernMysql(string host, int port, string database, string user, string password)
		: this(ConnectionStrings.Mysql(host, port, database, user, password)) { }

	[KernStatic("connect")] public static KResult<KernMysql, string> Connect(string connection)
	{
		try { return KResult<KernMysql, string>.Ok(KValue.Obj(new KernMysql(connection))); }
		catch (Exception e) when (e is MySqlException or InvalidOperationException or ArgumentException or System.Net.Sockets.SocketException)
		{
			return KResult<KernMysql, string>.Err(KValue.Str(e.Message));
		}
	}

	public SqlDialect Dialect => SqlDialect.Mysql;

	[KernMember] public KResult<long, string> Exec(string sql) => _core.Exec(sql, null);
	[KernMember] public KResult<long, string> Exec(string sql, KList<object> args) => _core.Exec(sql, args);
	[KernMember] public Task<KResult<long, string>> ExecAsync(string sql) => _core.ExecAsync(sql, null);
	[KernMember] public Task<KResult<long, string>> ExecAsync(string sql, KList<object> args) => _core.ExecAsync(sql, args);

	[KernMember] public KResult<KList<KDict<string, object>>, string> Query(string sql) => _core.Query(sql, null);
	[KernMember] public KResult<KList<KDict<string, object>>, string> Query(string sql, KList<object> args) => _core.Query(sql, args);
	[KernMember] public Task<KResult<KList<KDict<string, object>>, string>> QueryAsync(string sql) => _core.QueryAsync(sql, null);
	[KernMember] public Task<KResult<KList<KDict<string, object>>, string>> QueryAsync(string sql, KList<object> args) => _core.QueryAsync(sql, args);

	[KernMember] public KResult<object?, string> Scalar(string sql, KList<object> args) => _core.Scalar(sql, args);

	[KernProperty] public long LastId => _core.LastId;

	[KernMember] public void Begin() => _core.Begin();
	[KernMember] public void Commit() => _core.Commit();
	[KernMember] public void Rollback() => _core.Rollback();
	[KernMember] public void Close() => Dispose();

	public void Dispose() => _core.Dispose();
}
