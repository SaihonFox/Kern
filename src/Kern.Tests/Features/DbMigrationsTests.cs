using Kern.Binding.Resolution;
using Kern.Lib.Db.Sqlite;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Версионные миграции <c>kern.db</c> (spec §16): порядок версий, история, транзакции,
/// откат ошибочной миграции и повторный запуск без повторов.
/// </summary>
public sealed class DbMigrationsTests
{
	private const string Source = """
		use kern.db.sqlite[sqlite]
		use kern.db[migrator, migration]

		fun versions(list<str> items) -> str {
			var text = ""
			loop var item in items {
				text = if text == "" { item } else { text + "," + item }
			}
			text
		}

		fun apply_all(sqlite db) -> str {
			var mg = migrator(db)
			mg.add(migration("2", "insert", fun (database d) {
				d.exec("insert into items(name) values ('b')")
			}))
			mg.add(migration("1", "create", fun (database d) {
				d.exec("create table items(id integer primary key, name text)")
			}))
			match mg.up() {
				Ok(applied) => versions(applied)
				Err(e) => "ERR:" + e
			}
		}

		fun apply_again(sqlite db) -> str {
			var mg = migrator(db)
			mg.add(migration("2", "insert", fun (database d) {
				d.exec("insert into items(name) values ('b')")
			}))
			mg.add(migration("1", "create", fun (database d) {
				d.exec("create table items(id integer primary key, name text)")
			}))
			match mg.up() {
				Ok(applied) => versions(applied)
				Err(e) => "ERR:" + e
			}
		}

		fun pending_versions(sqlite db) -> str {
			var mg = migrator(db)
			mg.add(migration("2", "insert", fun (database d) {
				d.exec("insert into items(name) values ('b')")
			}))
			mg.add(migration("1", "create", fun (database d) {
				d.exec("create table items(id integer primary key, name text)")
			}))
			versions(mg.pending())
		}

		fun applied_versions(sqlite db) -> str {
			var mg = migrator(db)
			versions(mg.applied())
		}

		fun table_name(sqlite db) -> str {
			var mg = migrator(db)
			mg.table + "/" + versions(mg.applied())
		}

		fun items_count(sqlite db) -> i32 {
			match db.query("select id from items") {
				Ok(rows) => rows.len
				Err(_) => -1
			}
		}

		fun fail_apply(sqlite db) -> str {
			var mg = migrator(db)
			mg.add(migration("1", "ok", fun (database d) {
				d.exec("create table counter(n integer)")
			}))
			mg.add(migration("2", "bad", fun (database d) {
				panic("бум")
			}))
			mg.add(migration("3", "later", fun (database d) {
				d.exec("insert into counter(n) values (1)")
			}))
			match mg.up() {
				Ok(_) => "OK"
				Err(e) => e
			}
		}

		fun apply_later(sqlite db) -> str {
			var mg = migrator(db)
			mg.add(migration("3", "later", fun (database d) {
				d.exec("insert into counter(n) values (1)")
			}))
			match mg.up() {
				Ok(applied) => versions(applied)
				Err(e) => "ERR:" + e
			}
		}

		fun counter_count(sqlite db) -> i32 {
			match db.query("select n from counter") {
				Ok(rows) => rows.len
				Err(_) => -1
			}
		}
		""";

	[Fact]
	public void Up_applies_in_version_order_once()
	{
		(Interpreter interpreter, KernSqlite db) = NewDatabase();
		using (db)
		{
			KValue database = Database(db);

			Assert.Equal(KValue.Str("1,2"), interpreter.Run("apply_all", database));
			Assert.Equal(KValue.Str(""), interpreter.Run("apply_again", database));
			Assert.Equal(KValue.I32(1), interpreter.Run("items_count", database));
			Assert.Equal(KValue.Str("1,2"), interpreter.Run("applied_versions", database));
			Assert.Equal(KValue.Str(""), interpreter.Run("pending_versions", database));
		}
	}

	[Fact]
	public void Pending_and_applied_reflect_history()
	{
		(Interpreter interpreter, KernSqlite db) = NewDatabase();
		using (db)
		{
			KValue database = Database(db);

			Assert.Equal(KValue.Str("1,2"), interpreter.Run("pending_versions", database));
			Assert.Equal(KValue.Str(""), interpreter.Run("applied_versions", database));
			Assert.Equal(KValue.Str("1,2"), interpreter.Run("apply_all", database));
			Assert.Equal(KValue.Str(""), interpreter.Run("pending_versions", database));
			Assert.Equal(KValue.Str("1,2"), interpreter.Run("applied_versions", database));
		}
	}

	[Fact]
	public void Table_property_defaults_to_kern_migrations()
	{
		(Interpreter interpreter, KernSqlite db) = NewDatabase();
		using (db)
		{
			KValue database = Database(db);

			Assert.Equal(KValue.Str("kern_migrations/"), interpreter.Run("table_name", database));
			Assert.Equal(KValue.Str(""), interpreter.Run("applied_versions", database));
		}
	}

	[Fact]
	public void Failing_migration_rolls_back_and_is_not_recorded()
	{
		(Interpreter interpreter, KernSqlite db) = NewDatabase();
		using (db)
		{
			KValue database = Database(db);

			KValue failed = interpreter.Run("fail_apply", database);
			string message = failed.AsStr;
			Assert.Contains("миграция 2 (bad)", message);
			Assert.Contains("бум", message);
			Assert.Equal(KValue.Str("1"), interpreter.Run("applied_versions", database));
			Assert.Equal(KValue.I32(0), interpreter.Run("counter_count", database));
			Assert.Equal(KValue.Str("3"), interpreter.Run("apply_later", database));
			Assert.Equal(KValue.I32(1), interpreter.Run("counter_count", database));
		}
	}

	private static (Interpreter Interpreter, KernSqlite Db) NewDatabase()
	{
		Kern.Binding.Catalog.NativeCatalog catalog = StdPipeline.Catalog(typeof(KernSqlite).Assembly);
		Kern.Compiler.CodeGen.CodegenResult program = StdPipeline.Compile(Source, catalog);
		var interpreter = new Interpreter(program.Module, new NativeResolver(catalog));
		return (interpreter, new KernSqlite(interpreter, ":memory:"));
	}

	private static KValue Database(KernSqlite db) => KValue.Obj(new KNativeBox(db));
}
