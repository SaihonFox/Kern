using Kern.Binding.Resolution;
using Kern.Binding.Values;
using Kern.Lib.Db.Mysql;
using Kern.Lib.Db.Postgres;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Живые проверки PostgreSQL/MySQL. Запускаются только при заданных
/// <c>KERN_PG_URL</c>/<c>KERN_MYSQL_URL</c>, чтобы обычный <c>dotnet test</c>
/// не требовал серверов.
/// </summary>
public sealed class DbLiveTests
{
	[Fact]
	[Trait("Category", "db-live")]
	public void Postgres_crud_bool_transaction_and_last_id()
	{
		string? url = Environment.GetEnvironmentVariable("KERN_PG_URL");
		if (string.IsNullOrWhiteSpace(url))
			return;
		Interpreter interpreter = Interpreter();
		using KernPostgres db = new(interpreter, url);

		Check(db, "kern_live_pg");
	}

	[Fact]
	[Trait("Category", "db-live")]
	public void Mysql_crud_bool_transaction_and_last_id()
	{
		string? url = Environment.GetEnvironmentVariable("KERN_MYSQL_URL");
		if (string.IsNullOrWhiteSpace(url))
			return;
		Interpreter interpreter = Interpreter();
		using KernMysql db = new(interpreter, url);

		Check(db, "kern_live_mysql");
	}

	private static void Check(Kern.Lib.Db.Common.IKernDb db, string table)
	{
		Assert.True(db.Exec($"drop table if exists {table}", KValue.Nil).IsOk);
		KResult<long, string> created = db.Exec(
			$"create table {table}(id bigint primary key auto_increment, name text not null, active boolean not null)",
			KValue.Nil);
		if (!created.IsOk)
		{
			// PostgreSQL не понимает AUTO_INCREMENT: для него отдельный DDL.
			Assert.True(
				db.Exec(
					$"create table {table}(id bigserial primary key, name text not null, active boolean not null)",
					KValue.Nil).IsOk,
				"create table");
		}

		KResult<long, string> inserted = db.Exec(
			$"insert into {table}(name, active) values (?, ?)",
			KValue.Obj(new KList(null, [KValue.Str("on"), KValue.Bool(true)])));
		Assert.True(inserted.IsOk, inserted.IsOk ? "" : inserted.Err);
		Assert.True(db.LastId > 0);

		KResult<KValue, string> rows = db.Query(
			$"select name, active from {table} order by id",
			KValue.Nil);
		Assert.True(rows.IsOk, rows.IsOk ? "" : rows.Err);
		Assert.True(rows.Ok.Ref is KList);
		var list = (KList)rows.Ok.Ref!;
		Assert.Equal(1, list.Count);
		Assert.True(list.Items[0].Ref is KDict);
		var row = (KDict)list.Items[0].Ref!;
		Assert.Equal("on", row.Get(KValue.Str("name")).AsStr);
		KValue active = row.Get(KValue.Str("active"));
		Assert.True(active.Kind == ValueKind.Bool ? active.AsBool : active.AsI64 != 0);

		KResult<KValue, string> count = db.Scalar($"select count(*) from {table}", KValue.Nil);
		Assert.True(count.IsOk);
		Assert.Equal(1L, count.Ok.AsI64);

		db.Begin();
		Assert.True(db.Exec(
			$"insert into {table}(name, active) values (?, ?)",
			KValue.Obj(new KList(null, [KValue.Str("rolled"), KValue.Bool(false)]))).IsOk);
		db.Rollback();
		Assert.Equal(1L, db.Scalar($"select count(*) from {table}", KValue.Nil).Ok.AsI64);
	}

	private static Interpreter Interpreter()
	{
		var program = StdPipeline.Compile("fun main {}");
		return new Interpreter(program.Module, new NativeResolver(StdPipeline.Catalog()));
	}
}
