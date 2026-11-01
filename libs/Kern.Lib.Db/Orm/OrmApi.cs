using Kern.Binding.Attributes;
using Kern.Binding.Formatting;
using Kern.Binding.Values;
using Kern.Bytecode;
using Kern.Lib.Db.Common;
using Kern.Runtime.Core;
using Kern.Runtime.Json;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Db.Orm;

/// <summary>Тип-описание таблицы <c>table&lt;T&gt;</c>: колонки соответствуют экземплярным полям T.</summary>
[KernType("table", GenericArity = 1)]
public sealed class OrmTable
{
	private sealed record Column(string Name, int Slot, KType Type, bool IsKey);

	private readonly IKernDb _db;
	private readonly Interpreter _interpreter;
	private readonly KClassType _type;
	private readonly List<Column> _columns = [];

	public OrmTable(IKernDb db, Interpreter interpreter, KClassType type)
	{
		_db = db;
		_interpreter = interpreter;
		_type = type;
		foreach (FieldDef field in type.Definition.Fields)
		{
			if ((field.Flags & MemberFlags.Static) != 0 || (field.Flags & MemberFlags.Padding) != 0)
				continue;
			KType fieldType = interpreter.Types.FromSig(field.Type, type);
			_columns.Add(new Column(field.Name, field.Slot, fieldType, IsKey(field.Name, fieldType)));
		}

		Name = Plural(SnakeCase(type.Definition.Name));
	}

	public string Name { get; }

	private static bool IsKey(string name, KType type) =>
		name == "id" && type is KPrimType { Prim: >= PrimKind.I8 and <= PrimKind.U64 };

	private SqlDialect Dialect => _db.Dialect;

	private string Quote(string name) => Dialect.Quote(name);

	private string SqlType(KType type) => type switch
	{
		KPrimType { Prim: PrimKind.F32 or PrimKind.F64 or PrimKind.F16 } => Dialect.RealType,
		KPrimType { Prim: PrimKind.Str or PrimKind.Char } => Dialect.TextType,
		KPrimType { Prim: PrimKind.Bool } => Dialect.BoolType,
		KPrimType { Prim: >= PrimKind.I8 and <= PrimKind.U64 } => Dialect.IntegerType,
		KNullableType nullable => SqlType(nullable.Inner),
		_ => Dialect.TextType,
	};

	public KResult<long, string> Create()
	{
		var columns = _columns.Select(column => column.IsKey
			? $"{Quote(column.Name)} {Dialect.AutoKey}"
			: $"{Quote(column.Name)} {SqlType(column.Type)}{(column.Type is KNullableType ? "" : " NOT NULL")}");
		return _db.Exec($"CREATE TABLE IF NOT EXISTS {Quote(Name)} ({string.Join(", ", columns)})", KValue.Nil);
	}

	public KResult<long, string> Drop() =>
		_db.Exec($"DROP TABLE IF EXISTS {Quote(Name)}", KValue.Nil);

	/// <summary>
	/// Синхронизация схемы без версий: создаёт отсутствующую таблицу и добавляет
	/// недостающие колонки. Существующие колонки/данные не удаляются.
	/// </summary>
	public KResult<long, string> Migrate()
	{
		KResult<long, string> created = Create();
		if (!created.IsOk)
			return created;
		KResult<HashSet<string>, string> existing = ExistingColumns();
		if (!existing.IsOk)
			return KResult<long, string>.Failure(existing.Err);
		long added = 0;
		foreach (Column column in _columns.Where(column => !existing.Ok.Contains(column.Name)))
		{
			KResult<long, string> altered = _db.Exec(
				$"ALTER TABLE {Quote(Name)} ADD COLUMN {Quote(column.Name)} {SqlType(column.Type)}",
				KValue.Nil);
			if (!altered.IsOk)
				return altered;
			added++;
		}

		return KResult<long, string>.Success(added);
	}

	private KResult<HashSet<string>, string> ExistingColumns()
	{
		KResult<KValue, string> rows = Dialect.Name switch
		{
			"sqlite" => _db.Query($"PRAGMA table_info({Quote(Name)})", KValue.Nil),
			"postgres" => _db.Query(
				"select column_name as name from information_schema.columns " +
				"where table_name = ? and table_schema = current_schema()",
				KValue.Obj(new KList(null, [KValue.Str(Name)]))),
			_ => _db.Query(
				"select column_name as name from information_schema.columns " +
				"where table_name = ? and table_schema = database()",
				KValue.Obj(new KList(null, [KValue.Str(Name)]))),
		};
		if (!rows.IsOk)
			return KResult<HashSet<string>, string>.Failure(rows.Err);
		if (rows.Ok.Ref is not KList list)
			return KResult<HashSet<string>, string>.Failure("orm.migrate: ожидался список колонок");
		var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (KValue row in list.Items)
		{
			if (row.Ref is KDict dict && dict.TryGet(KValue.Str("name"), out KValue name) && name.Kind == ValueKind.Str)
				result.Add(name.AsStr);
		}

		return KResult<HashSet<string>, string>.Success(result);
	}

	internal KValue Field(KValue item, string name)
	{
		if (item.Ref is not KObject instance)
			throw new KernPanic("orm.relation: ожидался unit/type");
		Column? column = _columns.FirstOrDefault(column =>
			string.Equals(column.Name, name, StringComparison.Ordinal));
		if (column is null)
			throw new KernPanic($"orm.relation: у '{_type.Definition.Name}' нет поля '{name}'");
		return instance.GetField(column.Slot);
	}

	internal KValue FindBy(string column, KValue value)
	{
		KResult<KValue, string> result = Select(
			$" WHERE {Quote(column)} = ? LIMIT 1",
			KValue.Obj(new KList(null, [value])));
		if (!result.IsOk)
			throw new KernPanic("orm.relation: " + result.Err);
		var list = result.Ok.Ref as KList;
		return list is { Count: > 0 } ? list.Items[0] : KValue.Nil;
	}

	internal KValue ManyBy(string column, KValue value)
	{
		KResult<KValue, string> result = Select(
			$" WHERE {Quote(column)} = ?",
			KValue.Obj(new KList(null, [value])));
		if (!result.IsOk)
			throw new KernPanic("orm.relation: " + result.Err);
		return result.Ok;
	}

	public KResult<long, string> Insert(KValue item)
	{
		if (item.Ref is not KObject instance)
			return KResult<long, string>.Failure("orm.insert: ожидался unit/type");
		var columns = _columns
			.Where(column => !column.IsKey || !IsZero(instance.GetField(column.Slot)))
			.ToList();
		string sql = $"INSERT INTO {Quote(Name)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) " +
			$"VALUES ({string.Join(", ", columns.Select(_ => "?"))})";
		var arguments = new KList(null, columns.Select(column => ToDbValue(instance.GetField(column.Slot), column.Type)));
		KResult<long, string> result = _db.Exec(sql, KValue.Obj(arguments));
		if (!result.IsOk)
			return result;
		Column? key = _columns.FirstOrDefault(column => column.IsKey);
		if (key is not null && !IsZero(instance.GetField(key.Slot)))
			return KResult<long, string>.Success(instance.GetField(key.Slot).AsI64);
		return KResult<long, string>.Success(_db.LastId);
	}

	public KResult<long, string> Update(KValue item)
	{
		if (item.Ref is not KObject instance)
			return KResult<long, string>.Failure("orm.update: ожидался unit/type");
		Column? key = _columns.FirstOrDefault(column => column.IsKey);
		if (key is null)
			return KResult<long, string>.Failure($"у {_type.Definition.Name} нет поля id — update невозможен");
		var columns = _columns.Where(column => !column.IsKey).ToList();
		string sql = $"UPDATE {Quote(Name)} SET {string.Join(", ", columns.Select(column => $"{Quote(column.Name)} = ?"))} " +
			$"WHERE {Quote("id")} = ?";
		var arguments = columns.Select(column => ToDbValue(instance.GetField(column.Slot), column.Type))
			.Append(instance.GetField(key.Slot))
			.ToArray();
		return _db.Exec(sql, KValue.Obj(new KList(null, arguments)));
	}

	public KResult<long, string> Delete(long id) =>
		_db.Exec($"DELETE FROM {Quote(Name)} WHERE {Quote("id")} = ?", KValue.Obj(new KList(null, [KValue.I64(id)])));

	public KValue All() => WrapList(Select("", KValue.Nil));

	public KValue Where(string condition, KValue args) => WrapList(Select(" WHERE " + condition, args));

	public KValue Find(long id)
	{
		KResult<KValue, string> result = Select($" WHERE {Quote("id")} = ? LIMIT 1",
			KValue.Obj(new KList(null, [KValue.I64(id)])));
		if (!result.IsOk)
			return KernResults.Err(_interpreter, _interpreter.Types.Prim(PrimKind.Str), KValue.Str(result.Err));
		var list = result.Ok.Ref as KList;
		KValue value = list is { Count: > 0 } ? list.Items[0] : KValue.Nil;
		return KernResults.Ok(_interpreter, _interpreter.Types.Nullable(_type), value);
	}

	private KValue WrapList(KResult<KValue, string> result)
	{
		if (!result.IsOk)
			return KernResults.Err(_interpreter, _interpreter.Types.Prim(PrimKind.Str), KValue.Str(result.Err));
		KType listType = _interpreter.Types.Builtin(BuiltinGenericKind.List, _type);
		return KernResults.Ok(_interpreter, listType, result.Ok);
	}

	public KResult<long, string> Count()
	{
		KResult<KValue, string> result = _db.Scalar($"SELECT COUNT(*) FROM {Quote(Name)}", KValue.Nil);
		return result.IsOk
			? KResult<long, string>.Success(result.Ok.AsI64)
			: KResult<long, string>.Failure(result.Err);
	}

	private KResult<KValue, string> Select(string tail, KValue args)
	{
		KResult<KValue, string> rows = _db.Query($"SELECT * FROM {Quote(Name)}{tail}", args);
		if (!rows.IsOk)
			return rows;
		if (rows.Ok.Ref is not KList rowList)
			return KResult<KValue, string>.Failure("orm: ожидался список строк");
		var result = new List<KValue>();
		foreach (KValue row in rowList.Items)
		{
			if (row.Ref is not KDict dict)
				continue;
			var fields = new KValue[_type.Definition.Fields.Count];
			Array.Fill(fields, KValue.None);
			foreach (Column column in _columns)
			{
				if (dict.TryGet(KValue.Str(column.Name), out KValue value))
					fields[column.Slot] = FromDbValue(value, column.Type);
			}

			result.Add(KValue.Obj(new KObject(_type, fields)));
		}

		return KResult<KValue, string>.Success(KValue.Obj(new KList(null, result)));
	}

	private KValue ToDbValue(KValue value, KType type)
	{
		if (value.IsNilOrNone)
			return KValue.Nil;
		if (IsDatabasePrimitive(type))
			return value;
		return KValue.Str(JsonApi.ToStr(value));
	}

	private static bool IsDatabasePrimitive(KType type) => type switch
	{
		KNullableType nullable => IsDatabasePrimitive(nullable.Inner),
		KPrimType { Prim: PrimKind.Object or PrimKind.Nil or PrimKind.None or PrimKind.Void } => false,
		KPrimType => true,
		_ => false,
	};

	private KValue FromDbValue(KValue value, KType type)
	{
		if (type is KNullableType nullable)
			return value.IsNilOrNone ? KValue.Nil : FromDbValue(value, nullable.Inner);
		if (value.IsNilOrNone)
			return KValue.Nil;
		if (type is not KPrimType primitive)
		{
			if (value.Kind != ValueKind.Str)
				return value;
			try
			{
				return JsonApi.ParseInto(value.AsStr, type, _interpreter);
			}
			catch (Exception exception) when (
				exception is KernPanic or System.Text.Json.JsonException or InvalidOperationException)
			{
				return KValue.Nil;
			}
		}

		return primitive.Prim switch
		{
			PrimKind.Bool => KValue.Bool(value.IsInteger ? value.AsI64 != 0 : value.AsBool),
			PrimKind.Str => KValue.Str(value.Kind == ValueKind.Str ? value.AsStr : KernFormat.Value(value)),
			PrimKind.Char => KValue.Char(value.Kind == ValueKind.Str && value.AsStr.Length > 0 ? value.AsStr[0] : '\0'),
			PrimKind.I8 => KValue.I8((sbyte)value.AsI64),
			PrimKind.I16 => KValue.I16((short)value.AsI64),
			PrimKind.I32 => KValue.I32((int)value.AsI64),
			PrimKind.I64 => KValue.I64(value.AsI64),
			PrimKind.U8 => KValue.U8((byte)value.AsI64),
			PrimKind.U16 => KValue.U16((ushort)value.AsI64),
			PrimKind.U32 => KValue.U32((uint)value.AsI64),
			PrimKind.U64 => KValue.U64((ulong)value.AsI64),
			PrimKind.F32 => KValue.F32((float)value.AsF64),
			PrimKind.F64 => KValue.F64(value.IsFloat ? value.AsF64 : value.AsI64),
			_ => value,
		};
	}

	private static bool IsZero(KValue value) => value.IsInteger && value.AsI64 == 0;

	private static string SnakeCase(string text)
	{
		var builder = new System.Text.StringBuilder(text.Length + 4);
		for (var index = 0; index < text.Length; index++)
		{
			if (char.IsUpper(text[index]) && index > 0)
				builder.Append('_');
			builder.Append(char.ToLowerInvariant(text[index]));
		}

		return builder.ToString();
	}

	private static string Plural(string text) => text.EndsWith('s') ? text : text + "s";
}

/// <summary>
/// Связь двух таблиц: <c>belongs_to</c> (родитель хранит внешний ключ) или
/// <c>has_many</c> (дочерняя таблица хранит внешний ключ).
/// </summary>
[KernType("relation")]
public sealed class OrmRelation
{
	private readonly OrmTable _source;
	private readonly OrmTable _target;
	private readonly string _field;
	private readonly bool _many;

	internal OrmRelation(OrmTable source, OrmTable target, string field, bool many)
	{
		_source = source;
		_target = target;
		_field = field;
		_many = many;
	}

	public string Field => _field;
	public bool Many => _many;

	public KValue Find(KValue item)
	{
		if (_many)
			throw new KernPanic("orm.relation: has_many использует all(), а не find()");
		KValue key = _source.Field(item, _field);
		return key.IsNilOrNone ? KValue.Nil : _target.FindBy("id", key);
	}

	public KValue All(KValue item)
	{
		if (!_many)
			throw new KernPanic("orm.relation: belongs_to использует find(), а не all()");
		KValue key = _source.Field(item, "id");
		return key.IsNilOrNone ? KValue.Obj(new KList(null, [])) : _target.ManyBy(_field, key);
	}

	internal static OrmRelation Require(KNativeBox self) => self.Value switch
	{
		OrmRelation relation => relation,
		_ => throw new KernPanic($"relation: ожидалась связь, получено {self.Value.GetType().Name}"),
	};
}

[KernType("relation")]
public static class RelationApi
{
	[KernProperty]
	public static string Field(KNativeBox self) => OrmRelation.Require(self).Field;

	[KernProperty]
	public static bool Many(KNativeBox self) => OrmRelation.Require(self).Many;

	[KernMember]
	[KernReturn("T?")]
	public static KValue Find<T>(KNativeBox self, KValue item) => OrmRelation.Require(self).Find(item);

	[KernMember]
	[KernReturn("list<T>")]
	public static KValue All<T>(KNativeBox self, KValue item) => OrmRelation.Require(self).All(item);
}

[KernType("table", GenericArity = 1)]
public static class TableApi
{
	[KernProperty]
	public static string Name(KNativeBox self) => Require(self).Name;

	[KernMember]
	[KernReturn("result<i64, str>")]
	public static KResult<long, string> Create(KNativeBox self) => Require(self).Create();

	[KernMember]
	[KernReturn("result<i64, str>")]
	public static KResult<long, string> Migrate(KNativeBox self) => Require(self).Migrate();

	[KernMember]
	[KernReturn("result<i64, str>")]
	public static KResult<long, string> Drop(KNativeBox self) => Require(self).Drop();

	[KernMember]
	[KernReturn("result<i64, str>")]
	public static KResult<long, string> Insert(KNativeBox self, KValue item) => Require(self).Insert(item);

	[KernMember]
	[KernReturn("result<i64, str>")]
	public static KResult<long, string> Update(KNativeBox self, KValue item) => Require(self).Update(item);

	[KernMember]
	[KernReturn("result<i64, str>")]
	public static KResult<long, string> Delete(KNativeBox self, long id) => Require(self).Delete(id);

	[KernMember]
	[KernReturn("result<list<T>, str>")]
	public static KValue All(KNativeBox self) => Require(self).All();

	[KernMember]
	[KernReturn("result<list<T>, str>")]
	public static KValue Where(KNativeBox self, string condition, KValue args) =>
		Require(self).Where(condition, args);

	[KernMember]
	[KernReturn("result<T?, str>")]
	public static KValue Find(KNativeBox self, long id) => Require(self).Find(id);

	[KernMember]
	[KernReturn("result<i64, str>")]
	public static KResult<long, string> Count(KNativeBox self) => Require(self).Count();

	private static OrmTable Require(KNativeBox self) => self.Value switch
	{
		OrmTable table => table,
		_ => throw new KernPanic($"table: ожидалась таблица, получено {self.Value.GetType().Name}"),
	};
}

[KernType("orm")]
public static class OrmApi
{
	[KernStatic]
	[KernMember]
	[KernName("table")]
	[KernReturn("table<T>")]
	public static KValue Table<T>(Interpreter interpreter, KValue database, [KernTypeArgs] KType[] types)
	{
		if (database.Ref is not KNativeBox box || box.Value is not IKernDb db)
			throw new KernPanic("orm.table: ожидалась база данных (sqlite/postgres/mysql)");
		KType type = types.Length > 0 ? types[^1] : throw new KernPanic("orm.table: не передан тип");
		if (type is not KClassType classType)
			throw new KernPanic($"orm.table: '{type}' не является unit/type");
		return KValue.Obj(new KNativeBox(new OrmTable(db, interpreter, classType)));
	}

	[KernStatic]
	[KernMember]
	[KernName("belongs_to")]
	[KernReturn("relation")]
	public static OrmRelation BelongsTo<T1, T2>(KValue source, KValue target, string field) =>
		new(AsTable(source, "source"), AsTable(target, "target"), field, many: false);

	[KernStatic]
	[KernMember]
	[KernName("has_many")]
	[KernReturn("relation")]
	public static OrmRelation HasMany<T1, T2>(KValue source, KValue target, string field) =>
		new(AsTable(source, "source"), AsTable(target, "target"), field, many: true);

	private static OrmTable AsTable(KValue value, string what) =>
		value.Ref is KNativeBox box && box.Value is OrmTable table
			? table
			: throw new KernPanic($"orm.relation: {what} должен быть orm.table<T>");
}
