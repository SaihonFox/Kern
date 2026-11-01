using Kern.Binding.Resolution;
using Kern.Binding.Values;
using Kern.Lib.Db.Orm;
using Kern.Lib.Db.Sqlite;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>ORM migrations (add missing columns) and belongs_to/has_many relations.</summary>
public sealed class OrmRelationTests
{
	[Fact]
	public void Migrate_adds_missing_columns_without_dropping_data()
	{
		var interpreter = Interpreter("""
			unit user(i64 id, str name, i32 age)
			fun main {}
			""");
		Kern.Vm.Types.KClassType userType = Type(interpreter, "user");
		using var db = new KernSqlite(interpreter, ":memory:");
		Assert.True(db.Exec(
			"create table users(id integer primary key autoincrement, name text not null)",
			KValue.Nil).IsOk);
		Assert.True(db.Exec(
			"insert into users(name) values (?)",
			KValue.Obj(new Kern.Vm.Values.KList(null, [KValue.Str("old")]))).IsOk);

		var users = new OrmTable(db, interpreter, userType);
		KResult<long, string> migrated = users.Migrate();

		Assert.True(migrated.IsOk, migrated.IsOk ? "" : migrated.Err);
		Assert.Equal(1L, migrated.Ok);
		KResult<KValue, string> age = db.Scalar("select age from users", KValue.Nil);
		Assert.True(age.IsOk);
		Assert.True(age.Ok.IsNilOrNone);
	}

	[Fact]
	public void Belongs_to_and_has_many_link_rows()
	{
		var interpreter = Interpreter("""
			unit author(i64 id, str name)
			unit book(i64 id, str title, i64 author_id)
			fun main {}
			""");
		Kern.Vm.Types.KClassType authorType = Type(interpreter, "author");
		Kern.Vm.Types.KClassType bookType = Type(interpreter, "book");
		using var db = new KernSqlite(interpreter, ":memory:");
		var authors = new OrmTable(db, interpreter, authorType);
		var books = new OrmTable(db, interpreter, bookType);
		Assert.True(authors.Create().IsOk);
		Assert.True(books.Create().IsOk);

		var author = new KObject(authorType, [KValue.I64(0), KValue.Str("Аня")]);
		KResult<long, string> authorId = authors.Insert(KValue.Obj(author));
		Assert.True(authorId.IsOk);
		var storedAuthor = new KObject(authorType, [KValue.I64(authorId.Ok), KValue.Str("Аня")]);
		var book = new KObject(bookType,
			[KValue.I64(0), KValue.Str("Книга"), KValue.I64(authorId.Ok)]);
		Assert.True(books.Insert(KValue.Obj(book)).IsOk);

		OrmRelation belongs = OrmApi.BelongsTo<object, object>(
			KValue.Obj(new KNativeBox(books)),
			KValue.Obj(new KNativeBox(authors)),
			"author_id");
		KValue found = belongs.Find(KValue.Obj(book));
		var loadedAuthor = Assert.IsType<KObject>(found.Ref);
		Assert.Equal("Аня", loadedAuthor.GetField(1).AsStr);

		OrmRelation many = OrmApi.HasMany<object, object>(
			KValue.Obj(new KNativeBox(authors)),
			KValue.Obj(new KNativeBox(books)),
			"author_id");
		KValue all = many.All(KValue.Obj(storedAuthor));
		var list = Assert.IsType<Kern.Vm.Values.KList>(all.Ref);
		Assert.Single(list.Items);
	}

	private static Interpreter Interpreter(string source)
	{
		var program = StdPipeline.Compile(source);
		return new Interpreter(program.Module, new NativeResolver(StdPipeline.Catalog()));
	}

	private static Kern.Vm.Types.KClassType Type(Interpreter interpreter, string name) =>
		interpreter.Types.Named(interpreter.Module.TypePool.First(type => type.Name == name));
}
