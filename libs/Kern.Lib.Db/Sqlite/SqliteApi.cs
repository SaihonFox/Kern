using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Lib.Db.Common;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Microsoft.Data.Sqlite;

namespace Kern.Lib.Db.Sqlite;

[KernType("sqlite", Parent = "database")]
public sealed class KernSqlite : IKernDb, IDisposable
{
	private readonly DbCore _core;

	static KernSqlite() => SQLitePCL.Batteries_V2.Init();

	public KernSqlite(Interpreter interpreter, string path)
	{
		var connection = new SqliteConnection(path == ":memory:" ? "Data Source=:memory:" : $"Data Source={path}");
		connection.Open();
		_core = new DbCore(connection, SqlDialect.Sqlite, interpreter);
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

[KernType("sqlite")]
[KernCapability("db")]
public static class SqliteApi
{
	[KernCtor]
	[KernName("sqlite")]
	public static KernSqlite Open(Interpreter interpreter, string path) => new(interpreter, path);

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

	private static KernSqlite Require(KNativeBox self) => self.Value switch
	{
		KernSqlite db => db,
		_ => throw new KernPanic($"sqlite: ожидалась база, получено {self.Value.GetType().Name}"),
	};
}
