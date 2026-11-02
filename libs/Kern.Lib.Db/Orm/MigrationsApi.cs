using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Bytecode;
using Kern.Lib.Db.Common;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Db.Orm;

/// <summary>
/// Версионная миграция: версия, имя и Kern-действие <c>fun (db d) { … }</c>.
/// Экземпляр неизменяемый; действие выполняется в транзакции <c>migrator.up()</c>.
/// </summary>
[KernType("migration")]
public sealed class KernMigration
{
	public KernMigration(string version, string name, KValue action)
	{
		if (version.Length == 0)
			throw new KernPanic("migration: версия не может быть пустой");
		if (name.Length == 0)
			throw new KernPanic("migration: имя не может быть пустым");
		Version = version;
		Name = name;
		Action = action;
		ActionName = DisplayName(action);
	}

	/// <summary>Версия (сортируется как строка).</summary>
	public string Version { get; }

	/// <summary>Человекочитаемое имя миграции.</summary>
	public string Name { get; }

	/// <summary>Действие миграции; получает базу данных.</summary>
	public KValue Action { get; }

	/// <summary>Имя функции/замыкания для диагностики.</summary>
	public string ActionName { get; }

	private static string DisplayName(KValue action) => action.Ref switch
	{
		KClosure closure => closure.Function.Name,
		FunctionDef function => function.Name,
		_ => action.TypeName,
	};
}

/// <summary>
/// Применение версионных миграций: хранит добавленные миграции и историю в таблице
/// <c>kern_migrations</c>. Каждая миграция выполняется в собственной транзакции.
/// </summary>
[KernType("migrator")]
public sealed class KernMigrator
{
	private const string DefaultTable = "kern_migrations";

	private readonly IKernDb _db;
	private readonly KValue _database;
	private readonly Interpreter _interpreter;
	private readonly List<KernMigration> _migrations = [];

	public KernMigrator(IKernDb db, KValue database, Interpreter interpreter)
	{
		_db = db;
		_database = database;
		_interpreter = interpreter;
	}

	/// <summary>Имя таблицы истории (по умолчанию <c>kern_migrations</c>).</summary>
	public string Table { get; set; } = DefaultTable;

	/// <summary>Добавить миграцию; одинаковая версия дважды запрещена.</summary>
	public void Add(KernMigration migration)
	{
		if (_migrations.Any(item => string.Equals(item.Version, migration.Version, StringComparison.Ordinal)))
			throw new KernPanic($"мигратор: версия '{migration.Version}' уже добавлена");
		_migrations.Add(migration);
	}

	/// <summary>
	/// Применить неприменённые миграции по возрастанию версии. Каждая — в транзакции;
	/// ошибка откатывает только её и возвращает <c>Err("миграция …")</c>.
	/// </summary>
	public KValue Up()
	{
		KResult<long, string> ensured = EnsureHistory();
		if (!ensured.IsOk)
			return Fail("мигратор: " + ensured.Err);
		KResult<List<string>, string> history = ReadHistory();
		if (!history.IsOk)
			return Fail("мигратор: " + history.Err);

		var applied = new HashSet<string>(history.Ok, StringComparer.Ordinal);
		List<KernMigration> pending = _migrations
			.Where(migration => !applied.Contains(migration.Version))
			.OrderBy(migration => migration.Version, StringComparer.Ordinal)
			.ToList();
		var versions = new List<KValue>();
		foreach (KernMigration migration in pending)
		{
			string prefix = $"миграция {migration.Version} ({migration.Name}): ";
			try
			{
				_db.Begin();
				_interpreter.RunValue(migration.Action, _database);
				string timestamp = DateTime.UtcNow.ToString(
					"O", System.Globalization.CultureInfo.InvariantCulture);
				KResult<long, string> inserted = _db.Exec(
					$"INSERT INTO {Quote(Table)} ({Quote("version")}, {Quote("name")}, {Quote("applied_at")}) " +
					"VALUES (?, ?, ?)",
					KValue.Obj(new KList(null,
						[KValue.Str(migration.Version), KValue.Str(migration.Name), KValue.Str(timestamp)])));
				if (!inserted.IsOk)
				{
					_db.Rollback();
					return Fail(prefix + inserted.Err);
				}

				_db.Commit();
			}
			catch (KernPanic panic)
			{
				_db.Rollback();
				return Fail(prefix + panic.Message);
			}

			versions.Add(KValue.Str(migration.Version));
		}

		return Ok(ListType(), KValue.Obj(new KList(StringType(), versions)));
	}

	/// <summary>Версии добавленных, но ещё не применённых миграций (по возрастанию).</summary>
	public List<string> Pending()
	{
		KResult<long, string> ensured = EnsureHistory();
		if (!ensured.IsOk)
			throw new KernPanic("мигратор: " + ensured.Err);
		KResult<List<string>, string> history = ReadHistory();
		if (!history.IsOk)
			throw new KernPanic("мигратор: " + history.Err);
		var applied = new HashSet<string>(history.Ok, StringComparer.Ordinal);
		return _migrations
			.Where(migration => !applied.Contains(migration.Version))
			.Select(migration => migration.Version)
			.OrderBy(version => version, StringComparer.Ordinal)
			.ToList();
	}

	/// <summary>
	/// Версии из таблицы истории (по возрастанию). Если таблицы ещё нет, она создаётся —
	/// поэтому <c>applied()</c> на чистой базе возвращает пустой список, а не ошибку.
	/// </summary>
	public List<string> Applied()
	{
		KResult<long, string> ensured = EnsureHistory();
		if (!ensured.IsOk)
			throw new KernPanic("мигратор: " + ensured.Err);
		KResult<List<string>, string> history = ReadHistory();
		if (!history.IsOk)
			throw new KernPanic("мигратор: " + history.Err);
		return history.Ok;
	}

	private KResult<long, string> EnsureHistory() =>
		_db.Exec(
			$"CREATE TABLE IF NOT EXISTS {Quote(Table)} (" +
			$"{Quote("version")} {_db.Dialect.TextType} PRIMARY KEY, " +
			$"{Quote("name")} {_db.Dialect.TextType} NOT NULL, " +
			$"{Quote("applied_at")} {_db.Dialect.TextType} NOT NULL)",
			KValue.Nil);

	private KResult<List<string>, string> ReadHistory()
	{
		KResult<KValue, string> rows = _db.Query(
			$"SELECT {Quote("version")} FROM {Quote(Table)} ORDER BY {Quote("version")}",
			KValue.Nil);
		if (!rows.IsOk)
			return KResult<List<string>, string>.Failure(rows.Err);
		if (rows.Ok.Ref is not KList list)
			return KResult<List<string>, string>.Failure("мигратор: ожидался список версий");
		var versions = new List<string>(list.Items.Count);
		foreach (KValue row in list.Items)
		{
			if (row.Ref is KDict dict && dict.TryGet(KValue.Str("version"), out KValue version) &&
				version.Kind == ValueKind.Str)
			{
				versions.Add(version.AsStr);
			}
		}

		return KResult<List<string>, string>.Success(versions);
	}

	/// <summary>Тип <c>list&lt;str&gt;</c> этого интерпретатора (для членов <c>pending</c>/<c>applied</c>).</summary>
	internal KType StringListType() => ListType();

	private string Quote(string name) => _db.Dialect.Quote(name);

	private KType StringType() => _interpreter.Types.Prim(PrimKind.Str);

	private KType ListType() => _interpreter.Types.Builtin(BuiltinGenericKind.List, StringType());

	private KValue Ok(KType type, KValue value) => KernResults.Ok(_interpreter, type, value);

	private KValue Fail(string message) =>
		KernResults.Err(_interpreter, StringType(), KValue.Str(message));
}

/// <summary>Фабрики и члены типа <c>migration</c> (модуль <c>kern.db</c>).</summary>
[KernType("migration")]
public static class MigrationApi
{
	[KernCtor]
	[KernName("migration")]
	public static KernMigration Create(string version, string name, KValue action) =>
		new(version, name, action);
}

/// <summary>Фабрики и члены типа <c>migrator</c> (модуль <c>kern.db</c>).</summary>
[KernType("migrator")]
public static class MigratorApi
{
	[KernCtor]
	[KernName("migrator")]
	public static KernMigrator Create(Interpreter interpreter, KValue database)
	{
		if (database.Ref is not KNativeBox box || box.Value is not IKernDb db)
			throw new KernPanic("migrator: ожидалась база данных (sqlite/postgres/mysql)");
		return new KernMigrator(db, database, interpreter);
	}

	[KernMember]
	public static void Add(KNativeBox self, KValue migration)
	{
		Require(self).Add(AsMigration(migration));
	}

	[KernMember]
	[KernReturn("result<list<str>, str>")]
	public static KValue Up(KNativeBox self) => Require(self).Up();

	[KernMember]
	[KernReturn("list<str>")]
	public static KValue Pending(KNativeBox self)
	{
		KernMigrator migrator = Require(self);
		return KValue.Obj(new KList(
			migrator.StringListType(),
			[.. migrator.Pending().Select(KValue.Str)]));
	}

	[KernMember]
	[KernReturn("list<str>")]
	public static KValue Applied(KNativeBox self)
	{
		KernMigrator migrator = Require(self);
		return KValue.Obj(new KList(
			migrator.StringListType(),
			[.. migrator.Applied().Select(KValue.Str)]));
	}

	[KernProperty]
	public static string Table(KNativeBox self) => Require(self).Table;

	[KernProperty]
	public static void Table(KNativeBox self, string value)
	{
		if (value.Length == 0)
			throw new KernPanic("migrator.table: имя таблицы не может быть пустым");
		Require(self).Table = value;
	}

	private static KernMigrator Require(KNativeBox self) => self.Value switch
	{
		KernMigrator migrator => migrator,
		_ => throw new KernPanic($"migrator: ожидался мигратор, получено {self.Value.GetType().Name}"),
	};

	private static KernMigration AsMigration(KValue value) =>
		value.Ref is KNativeBox box && box.Value is KernMigration migration
			? migration
			: throw new KernPanic("migrator.add: ожидалась миграция (migration)");
}
