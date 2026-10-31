using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Bytecode;
using Kern.Runtime.Core;
using Kern.Runtime.Json;
using Kern.Lib.Db.Common;
using Kern.Lib.Db.Mysql;
using Kern.Lib.Db.Postgres;
using Kern.Lib.Db.Sqlite;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Db.Orm;

/// <summary>
/// Мини-ORM поверх SQLite: таблица строится по полям типа (unit/класса). Поле `id` с целым типом — первичный ключ с автонумерацией.
/// <code>
/// unit User(i64 id, str name, i32 age)
/// sqlite db(":memory:")
/// table<User> users = orm.table<User>(db)
/// users.create()
/// users.insert(User(0, "Аня", 25))           // id назначается базой
/// match users.where("age > ?", [20]) { Ok(list) -> println(list); Err(e) -> println(e) }
/// </code>
/// Типы колонок: целые → INTEGER, f32/f64 → REAL, bool → INTEGER, str/char → TEXT; остальные (вложенные типы, списки) — JSON в TEXT.
/// </summary>
[KernType("orm")]
public static class OrmApi
{
	[KernStatic] public static OrmTable<T> Table<T>(KernSqlite db, [KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs) => Make<T>(db, vm, typeArgs);
	[KernStatic] public static OrmTable<T> Table<T>(KernPostgres db, [KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs) => Make<T>(db, vm, typeArgs);
	[KernStatic] public static OrmTable<T> Table<T>(KernMysql db, [KernVm] Interpreter vm, [KernTypeArgs] KType[] typeArgs) => Make<T>(db, vm, typeArgs);

	private static OrmTable<T> Make<T>(IKernDb db, Interpreter vm, KType[] typeArgs)
	{
		var type = Kv.Last(typeArgs) as KClassType ?? throw new KernPanic("orm.table: T должен быть классом или unit");
		return new OrmTable<T>(db, vm, type);
	}
}

/// <summary>Таблица записей типа T.</summary>
[KernType("table")]
public sealed class OrmTable<T>
{
	private readonly IKernDb _db;
	private readonly Interpreter _vm;
	private readonly KClassType _type;
	private readonly List<(string Name, int Slot, KType Type)> _columns = [];

	internal OrmTable(IKernDb db, Interpreter vm, KClassType type)
	{
		_db = db;
		_vm = vm;
		_type = type;
		foreach (var f in vm.Info.Classes[type.DefIndex].AllFields)
			_columns.Add((f.Name, f.Slot, vm.Types.Resolve(f.Type, type.Args, [])));
		Name = Plural(type.Def.Name);
	}

	[KernProperty] public string Name { get; }

	private static string Plural(string typeName)
	{
		var s = SnakeCase(typeName);
		return s.EndsWith('s') ? s : s + "s";
	}

	private static string SnakeCase(string s)
	{
		var sb = new StringBuilder();
		for (int i = 0; i < s.Length; i++)
		{
			if (char.IsUpper(s[i]) && i > 0) sb.Append('_');
			sb.Append(char.ToLowerInvariant(s[i]));
		}
		return sb.ToString();
	}

	private bool IsKey((string Name, int Slot, KType Type) c) =>
		c.Name == "id" && c.Type is KPrimType { Kind: >= PrimKind.I8 and <= PrimKind.U64 };

	private SqlDialect Dialect => _db.Dialect;
	private string Q(string name) => Dialect.Quote(name);

	private string SqlType(KType t) => t switch
	{
		KPrimType { Kind: PrimKind.F32 or PrimKind.F64 or PrimKind.F16 } => Dialect.RealType,
		KPrimType { Kind: PrimKind.Str or PrimKind.Char } => Dialect.TextType,
		KPrimType { Kind: PrimKind.Bool } => Dialect.IntegerType,
		KPrimType p when p.Kind is >= PrimKind.I8 and <= PrimKind.U64 => Dialect.IntegerType,
		KNullableType n => SqlType(n.Inner),
		_ => Dialect.TextType,
	};

	/// <summary>CREATE TABLE IF NOT EXISTS по полям типа.</summary>
	[KernMember] public KResult<long, string> Create()
	{
		var cols = _columns.Select(c => IsKey(c)
			? $"{Q(c.Name)} {Dialect.AutoKey}"
			: $"{Q(c.Name)} {SqlType(c.Type)}{(c.Type is KNullableType ? "" : " NOT NULL")}");
		return _db.Exec($"CREATE TABLE IF NOT EXISTS {Q(Name)} ({string.Join(", ", cols)})", NoArgs());
	}

	[KernMember] public KResult<long, string> Drop() => _db.Exec($"DROP TABLE IF EXISTS {Q(Name)}", NoArgs());

	private static KList<object> NoArgs() => Kv.List<object>([]);

	private KValue ToDbValue(KValue v, KType type)
	{
		if (v.IsNilOrNone) return KValue.Nil;
		return SqlType(type) == Dialect.TextType && type is not KPrimType { Kind: PrimKind.Str or PrimKind.Char }
			&& !(type is KNullableType { Inner: KPrimType })
			? KValue.Str(JsonApi.ToStr(_vm, v))
			: v;
	}

	/// <summary>Добавляет запись; результат — присвоенный id (для таблиц с ключом `id`).</summary>
	[KernMember] public KResult<long, string> Insert(T item)
	{
		var obj = (KObject)Kv.Of(item).Ref!;
		var cols = _columns.Where(c => !IsKey(c) || !obj.Fields[c.Slot].IsInteger || obj.Fields[c.Slot].AsI64 != 0).ToList();
		string sql = $"INSERT INTO {Q(Name)} ({string.Join(", ", cols.Select(c => Q(c.Name)))}) VALUES ({string.Join(", ", cols.Select(_ => "?"))})";
		var args = Kv.List<object>(cols.Select(c => ToDbValue(obj.Fields[c.Slot], c.Type)));
		var r = _db.Exec(sql, args);
		if (!r.Inner.IsOk) return r;
		// id задан явно — он и есть результат; иначе спрашиваем у базы (lastval/last_insert_id/last_insert_rowid)
		var key = _columns.FirstOrDefault(IsKey);
		if (key.Name is not null && obj.Fields[key.Slot].IsInteger && obj.Fields[key.Slot].AsI64 != 0)
			return KResult<long, string>.Ok(obj.Fields[key.Slot]);
		return KResult<long, string>.Ok(KValue.I64(_db.LastId));
	}

	/// <summary>Обновляет запись по `id`; результат — число изменённых строк.</summary>
	[KernMember] public KResult<long, string> Update(T item)
	{
		var obj = (KObject)Kv.Of(item).Ref!;
		var key = _columns.FirstOrDefault(IsKey);
		if (key.Name is null) return Kv.Err<long>($"в {_type.Def.Name} нет поля id — update невозможен");
		var cols = _columns.Where(c => !IsKey(c)).ToList();
		string sql = $"UPDATE {Q(Name)} SET {string.Join(", ", cols.Select(c => $"{Q(c.Name)} = ?"))} WHERE {Q("id")} = ?";
		var values = cols.Select(c => ToDbValue(obj.Fields[c.Slot], c.Type)).Append(obj.Fields[key.Slot]);
		return _db.Exec(sql, Kv.List<object>(values));
	}

	[KernMember] public KResult<long, string> Delete(long id) => _db.Exec($"DELETE FROM {Q(Name)} WHERE {Q("id")} = ?", Kv.List<object>([KValue.I64(id)]));

	[KernMember] public KResult<KList<T>, string> All() => Select("", Kv.List<object>([]));

	/// <summary>Записи по условию SQL: <c>users.where("age > ? and name like ?", [20, "А%"])</c>.</summary>
	[KernMember] public KResult<KList<T>, string> Where(string condition, KList<object> args) => Select(" WHERE " + condition, args);

	[KernMember] public KResult<T?, string> Find(long id)
	{
		var r = Select($" WHERE {Q("id")} = ? LIMIT 1", Kv.List<object>([KValue.I64(id)]));
		if (!r.Inner.IsOk) return KResult<T?, string>.Err(r.Inner.Value);
		var items = ((KListObject)r.Inner.Value.Ref!).Items;
		return KResult<T?, string>.Ok(items.Count > 0 ? items[0] : KValue.Nil);
	}

	[KernMember] public KResult<long, string> Count()
	{
		var r = _db.Scalar($"SELECT COUNT(*) FROM {Q(Name)}", NoArgs());
		return r.Inner.IsOk ? KResult<long, string>.Ok(r.Inner.Value) : KResult<long, string>.Err(r.Inner.Value);
	}

	private KResult<KList<T>, string> Select(string tail, KList<object> args)
	{
		var rows = _db.Query($"SELECT * FROM {Q(Name)}{tail}", args);
		if (!rows.Inner.IsOk) return KResult<KList<T>, string>.Err(rows.Inner.Value);
		var result = new List<KValue>();
		foreach (var row in ((KListObject)rows.Inner.Value.Ref!).Items)
		{
			var dict = ((KDictObject)row.Ref!).Items;
			var fields = new KValue[_vm.Info.Classes[_type.DefIndex].FieldCount];
			foreach (var c in _columns)
				if (dict.TryGetValue(KValue.Str(c.Name), out var v)) fields[c.Slot] = FromDbValue(v, c.Type);
			result.Add(KValue.Obj(new KObject(_type, fields)));
		}
		return KResult<KList<T>, string>.Ok(KValue.Obj(Kv.List<T>(_type, result).Inner));
	}

	private KValue FromDbValue(KValue v, KType type)
	{
		if (type is KNullableType n) return v.IsNilOrNone ? KValue.Nil : FromDbValue(v, n.Inner);
		if (v.IsNilOrNone) return KValue.Nil;
		if (type is KPrimType p)
			return p.Kind switch
			{
				PrimKind.Bool => KValue.Bool(v.AsI64 != 0),
				PrimKind.Str => KValue.Str(v.Kind == ValueKind.Str ? v.AsStr : ValueOps.ToStr(v)),
				PrimKind.Char => KValue.Char(v.AsStr is { Length: > 0 } s ? s[0] : '\0'),
				PrimKind.I8 => KValue.I8((sbyte)v.AsI64), PrimKind.I16 => KValue.I16((short)v.AsI64),
				PrimKind.I32 => KValue.I32((int)v.AsI64), PrimKind.I64 => KValue.I64(v.AsI64),
				PrimKind.U8 => KValue.U8((byte)v.AsI64), PrimKind.U16 => KValue.U16((ushort)v.AsI64),
				PrimKind.U32 => KValue.U32((uint)v.AsI64), PrimKind.U64 => KValue.U64((ulong)v.AsI64),
				PrimKind.F32 => KValue.F32((float)v.AsF64), PrimKind.F64 => KValue.F64(v.IsFloat ? v.AsF64 : v.AsI64),
				_ => v,
			};
		return v.Kind == ValueKind.Str ? JsonApi.FromJsonText(_vm, v.AsStr, type) : v;
	}
}
