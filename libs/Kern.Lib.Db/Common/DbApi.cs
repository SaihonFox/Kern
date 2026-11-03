using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Db.Common;

/// <summary>
/// Общий тип <c>db</c> для действий миграций (<c>fun (database d) { … }</c>): те же члены, что у
/// <c>sqlite</c>/<c>postgres</c>/<c>mysql</c>, но без привязки к конкретному драйверу.
/// </summary>
[KernType("database")]
[KernCapability("db")]
public static class DbApi
{
	[KernMember]
	public static KResult<long, string> Exec(KNativeBox self, string sql) => Require(self).Exec(sql, KValue.Nil);

	[KernMember]
	public static KResult<long, string> Exec(KNativeBox self, string sql, KValue args) => Require(self).Exec(sql, args);

	[KernMember]
	[KernReturn("task<result<i64, str>>")]
	public static Task<KResult<long, string>> ExecAsync(KNativeBox self, string sql) =>
		Require(self).ExecAsync(sql, KValue.Nil);

	[KernMember]
	[KernReturn("task<result<i64, str>>")]
	public static Task<KResult<long, string>> ExecAsync(KNativeBox self, string sql, KValue args) =>
		Require(self).ExecAsync(sql, args);

	[KernMember]
	[KernReturn("result<list<dict<str, object>>, str>")]
	public static KValue Query(KNativeBox self, string sql) => Require(self).QueryValue(sql, KValue.Nil);

	[KernMember]
	[KernReturn("result<list<dict<str, object>>, str>")]
	public static KValue Query(KNativeBox self, string sql, KValue args) => Require(self).QueryValue(sql, args);

	[KernMember]
	[KernReturn("task<result<list<dict<str, object>>, str>>")]
	public static Task<KValue> QueryAsync(KNativeBox self, string sql) =>
		Require(self).QueryAsyncValue(sql, KValue.Nil);

	[KernMember]
	[KernReturn("task<result<list<dict<str, object>>, str>>")]
	public static Task<KValue> QueryAsync(KNativeBox self, string sql, KValue args) =>
		Require(self).QueryAsyncValue(sql, args);

	[KernMember]
	[KernReturn("result<object, str>")]
	public static KResult<KValue, string> Scalar(KNativeBox self, string sql) => Require(self).Scalar(sql, KValue.Nil);

	[KernMember]
	[KernReturn("result<object, str>")]
	public static KResult<KValue, string> Scalar(KNativeBox self, string sql, KValue args) =>
		Require(self).Scalar(sql, args);

	[KernProperty]
	public static long LastId(KNativeBox self) => Require(self).LastId;

	[KernMember]
	public static void Begin(KNativeBox self) => Require(self).Begin();

	[KernMember]
	public static void Commit(KNativeBox self) => Require(self).Commit();

	[KernMember]
	public static void Rollback(KNativeBox self) => Require(self).Rollback();

	[KernMember]
	public static void Close(KNativeBox self)
	{
		IKernDb database = Require(self);
		if (database is IDisposable disposable)
			disposable.Dispose();
	}

	private static IKernDb Require(KNativeBox self) => self.Value switch
	{
		IKernDb database => database,
		_ => throw new KernPanic($"db: ожидалась база данных, получено {self.Value.GetType().Name}"),
	};
}
