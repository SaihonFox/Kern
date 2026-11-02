using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Lib.Db.Common;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Npgsql;

namespace Kern.Lib.Db.Postgres;

[KernType("postgres", Parent = "database")]
public sealed class KernPostgres : IKernDb, IDisposable
{
	private readonly DbCore _core;

	public KernPostgres(Interpreter interpreter, string connection)
	{
		var conn = new NpgsqlConnection(ConnectionStrings.Postgres(connection));
		conn.Open();
		_core = new DbCore(conn, SqlDialect.Postgres, interpreter);
	}

	public KernPostgres(Interpreter interpreter, string host, int port, string database, string user, string password)
		: this(interpreter, ConnectionStrings.Postgres(host, port, database, user, password))
	{
	}

	public SqlDialect Dialect => _core.Dialect;
	public long LastId => _core.LastId;
	public KResult<long, string> Exec(string sql, KValue args) => _core.Exec(sql, args);
	public KResult<KValue, string> Query(string sql, KValue args) => _core.Query(sql, args);
	public KValue QueryValue(string sql, KValue args) => _core.QueryValue(sql, args);
	public KResult<KValue, string> Scalar(string sql, KValue args) => _core.Scalar(sql, args);
	public Task<KResult<long, string>> ExecAsync(string sql, KValue args) => _core.ExecAsync(sql, args);
	public Task<KResult<KValue, string>> QueryAsync(string sql, KValue args) => _core.QueryAsync(sql, args);
	public Task<KValue> QueryAsyncValue(string sql, KValue args) => _core.QueryAsyncValue(sql, args);
	public void Begin() => _core.Begin();
	public void Commit() => _core.Commit();
	public void Rollback() => _core.Rollback();
	public void Dispose() => _core.Dispose();
}

[KernType("postgres")]
public static class PostgresApi
{
	[KernCtor]
	[KernName("postgres")]
	public static KernPostgres Open(Interpreter interpreter, string connection) => new(interpreter, connection);

	[KernCtor]
	[KernName("postgres")]
	public static KernPostgres Open(
		Interpreter interpreter,
		string host,
		int port,
		string database,
		string user,
		string password) =>
		new(interpreter, host, port, database, user, password);

	[KernStatic]
	[KernMember]
	[KernName("connect")]
	[KernReturn("result<postgres, str>")]
	public static KResult<KernPostgres, string> Connect(Interpreter interpreter, string connection)
	{
		try
		{
			return KResult<KernPostgres, string>.Success(new KernPostgres(interpreter, connection));
		}
		catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or ArgumentException or System.Net.Sockets.SocketException)
		{
			return KResult<KernPostgres, string>.Failure(exception.Message);
		}
	}

	[KernStatic]
	[KernMember]
	[KernName("connect")]
	[KernReturn("result<postgres, str>")]
	public static KResult<KernPostgres, string> Connect(
		Interpreter interpreter,
		string host,
		int port,
		string database,
		string user,
		string password) =>
		Connect(interpreter, ConnectionStrings.Postgres(host, port, database, user, password));

	[KernMember]
	public static KResult<long, string> Exec(KNativeBox self, string sql) => Require(self).Exec(sql, KValue.Nil);

	[KernMember]
	public static KResult<long, string> Exec(KNativeBox self, string sql, KValue args) => Require(self).Exec(sql, args);

	[KernMember]
	[KernReturn("task<result<i64, str>>")]
	public static Task<KResult<long, string>> ExecAsync(KNativeBox self, string sql) => Require(self).ExecAsync(sql, KValue.Nil);

	[KernMember]
	[KernReturn("task<result<i64, str>>")]
	public static Task<KResult<long, string>> ExecAsync(KNativeBox self, string sql, KValue args) => Require(self).ExecAsync(sql, args);

	[KernMember]
	[KernReturn("result<list<dict<str, object>>, str>")]
	public static KValue Query(KNativeBox self, string sql) => Require(self).QueryValue(sql, KValue.Nil);

	[KernMember]
	[KernReturn("result<list<dict<str, object>>, str>")]
	public static KValue Query(KNativeBox self, string sql, KValue args) => Require(self).QueryValue(sql, args);

	[KernMember]
	[KernReturn("task<result<list<dict<str, object>>, str>>")]
	public static Task<KValue> QueryAsync(KNativeBox self, string sql) => Require(self).QueryAsyncValue(sql, KValue.Nil);

	[KernMember]
	[KernReturn("task<result<list<dict<str, object>>, str>>")]
	public static Task<KValue> QueryAsync(KNativeBox self, string sql, KValue args) => Require(self).QueryAsyncValue(sql, args);

	[KernMember]
	[KernReturn("result<object, str>")]
	public static KResult<KValue, string> Scalar(KNativeBox self, string sql) => Require(self).Scalar(sql, KValue.Nil);

	[KernMember]
	[KernReturn("result<object, str>")]
	public static KResult<KValue, string> Scalar(KNativeBox self, string sql, KValue args) => Require(self).Scalar(sql, args);

	[KernProperty]
	public static long LastId(KNativeBox self) => Require(self).LastId;

	[KernMember]
	public static void Begin(KNativeBox self) => Require(self).Begin();

	[KernMember]
	public static void Commit(KNativeBox self) => Require(self).Commit();

	[KernMember]
	public static void Rollback(KNativeBox self) => Require(self).Rollback();

	[KernMember]
	public static void Close(KNativeBox self) => Require(self).Dispose();

	private static KernPostgres Require(KNativeBox self) => self.Value switch
	{
		KernPostgres db => db,
		_ => throw new KernPanic($"postgres: ожидалась база, получено {self.Value.GetType().Name}"),
	};
}
