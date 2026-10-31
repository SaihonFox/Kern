using System.Data.Common;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Db.Common;

/// <summary>
/// Общая часть драйверов БД поверх ADO.NET (<see cref="DbConnection"/>): команды с параметрами, чтение строк, транзакции, async-варианты.
/// Ошибки SQL возвращаются как Err, а не исключения.
/// </summary>
public sealed class DbCore(DbConnection connection, SqlDialect dialect) : IDisposable
{
	private DbTransaction? _tx;

	public DbConnection Connection { get; } = connection;
	public SqlDialect Dialect { get; } = dialect;

	private DbCommand Command(string sql, KList<object>? args)
	{
		var cmd = Connection.CreateCommand();
		cmd.Transaction = _tx;
		cmd.CommandText = Dialect.Rewrite(sql);
		if (args is { } list)
			for (int i = 0; i < list.Count; i++)
			{
				var p = cmd.CreateParameter();
				if (Dialect.ParameterName is { } name) p.ParameterName = name(i);
				p.Value = ToDb(list[i]);
				cmd.Parameters.Add(p);
			}
		return cmd;
	}

	private static bool IsSqlError(Exception e) => e is DbException or InvalidOperationException;

	public static object ToDb(KValue v) => v.Kind switch
	{
		ValueKind.Nil or ValueKind.None => DBNull.Value,
		ValueKind.Str => v.AsStr,
		ValueKind.Bool => v.Bits != 0 ? 1L : 0L,
		_ when v.IsInteger => v.AsI64,
		_ when v.IsNumber => v.AsF64,
		_ => ValueOps.ToStr(v),
	};

	public static KValue FromDb(object value) => value switch
	{
		DBNull => KValue.Nil,
		bool b => KValue.I64(b ? 1 : 0),
		sbyte or byte or short or ushort or int or uint or long => KValue.I64(Convert.ToInt64(value)),
		ulong ul => KValue.I64(unchecked((long)ul)),
		float or double => KValue.F64(Convert.ToDouble(value)),
		decimal d => KValue.F64((double)d),
		string s => KValue.Str(s),
		byte[] bytes => KValue.Str(Convert.ToBase64String(bytes)),
		DateTime dt => KValue.Str(dt.ToString("O")),
		DateTimeOffset dto => KValue.Str(dto.ToString("O")),
		Guid g => KValue.Str(g.ToString()),
		_ => KValue.Str(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""),
	};

	// ---------- выполнение ----------

	public KResult<long, string> Exec(string sql, KList<object>? args)
	{
		try
		{
			using var cmd = Command(sql, args);
			return KResult<long, string>.Ok(KValue.I64(cmd.ExecuteNonQuery()));
		}
		catch (Exception e) when (IsSqlError(e)) { return Kv.Err<long>(e.Message); }
	}

	public async Task<KResult<long, string>> ExecAsync(string sql, KList<object>? args)
	{
		try
		{
			await using var cmd = Command(sql, args);
			return KResult<long, string>.Ok(KValue.I64(await cmd.ExecuteNonQueryAsync()));
		}
		catch (Exception e) when (IsSqlError(e)) { return Kv.Err<long>(e.Message); }
	}

	private static KValue Row(DbDataReader reader)
	{
		var row = new KDictObject(TypeFactory.Str, TypeFactory.Object);
		for (int i = 0; i < reader.FieldCount; i++)
			row.Items[KValue.Str(reader.GetName(i))] = FromDb(reader.GetValue(i));
		return KValue.Obj(row);
	}

	private static KResult<KList<KDict<string, object>>, string> Rows(List<KValue> rows) =>
		Kv.Ok<KList<KDict<string, object>>>(KValue.Obj(Kv.List<KDict<string, object>>(rows).Inner));

	public KResult<KList<KDict<string, object>>, string> Query(string sql, KList<object>? args)
	{
		try
		{
			using var cmd = Command(sql, args);
			using var reader = cmd.ExecuteReader();
			var rows = new List<KValue>();
			while (reader.Read()) rows.Add(Row(reader));
			return Rows(rows);
		}
		catch (Exception e) when (IsSqlError(e)) { return Kv.Err<KList<KDict<string, object>>>(e.Message); }
	}

	public async Task<KResult<KList<KDict<string, object>>, string>> QueryAsync(string sql, KList<object>? args)
	{
		try
		{
			await using var cmd = Command(sql, args);
			await using var reader = await cmd.ExecuteReaderAsync();
			var rows = new List<KValue>();
			while (await reader.ReadAsync()) rows.Add(Row(reader));
			return Rows(rows);
		}
		catch (Exception e) when (IsSqlError(e)) { return Kv.Err<KList<KDict<string, object>>>(e.Message); }
	}

	public KResult<object?, string> Scalar(string sql, KList<object>? args)
	{
		try
		{
			using var cmd = Command(sql, args);
			var v = cmd.ExecuteScalar();
			return KResult<object?, string>.Ok(v is null ? KValue.Nil : FromDb(v));
		}
		catch (Exception e) when (IsSqlError(e)) { return Kv.Err<object?>(e.Message); }
	}

	public long LastId
	{
		get
		{
			using var cmd = Connection.CreateCommand();
			cmd.Transaction = _tx;
			cmd.CommandText = Dialect.LastIdSql;
			return Convert.ToInt64(cmd.ExecuteScalar());
		}
	}

	// ---------- транзакции ----------

	public void Begin() => _tx ??= Connection.BeginTransaction();

	public void Commit()
	{
		_tx?.Commit();
		_tx?.Dispose();
		_tx = null;
	}

	public void Rollback()
	{
		_tx?.Rollback();
		_tx?.Dispose();
		_tx = null;
	}

	public void Dispose()
	{
		_tx?.Dispose();
		Connection.Dispose();
	}
}
