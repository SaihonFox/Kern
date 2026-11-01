using System.Data.Common;
using Kern.Binding.Formatting;
using Kern.Binding.Values;
using Kern.Bytecode;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Lib.Db.Common;
public interface IKernDb
{
	SqlDialect Dialect { get; }
	long LastId { get; }
	KResult<long, string> Exec(string sql, KValue args);
	KResult<KValue, string> Query(string sql, KValue args);
	KValue QueryValue(string sql, KValue args);
	KResult<KValue, string> Scalar(string sql, KValue args);
	Task<KResult<long, string>> ExecAsync(string sql, KValue args);
	Task<KResult<KValue, string>> QueryAsync(string sql, KValue args);
	Task<KValue> QueryAsyncValue(string sql, KValue args);
	void Begin();
	void Commit();
	void Rollback();
}

/// <summary>SQL-диалект: кавычки, типы, плейсхолдеры, last-id.</summary>
public sealed class SqlDialect
{
	public required string Name { get; init; }
	public required char QuoteOpen { get; init; }
	public required char QuoteClose { get; init; }
	public required string IntegerType { get; init; }
	public required string RealType { get; init; }
	public required string TextType { get; init; }
	public required string BoolType { get; init; }
	public required string AutoKey { get; init; }
	public Func<int, string>? Placeholder { get; init; }
	public Func<int, string>? ParameterName { get; init; }
	public required string LastIdSql { get; init; }

	public string Quote(string name) =>
		QuoteOpen + name.Replace(QuoteClose.ToString(), new string(QuoteClose, 2), StringComparison.Ordinal) + QuoteClose;

	public string Rewrite(string sql)
	{
		if (Placeholder is null)
			return sql;
		var builder = new System.Text.StringBuilder(sql.Length + 8);
		var index = 0;
		var quote = false;
		var doubleQuote = false;
		var lineComment = false;
		var blockComment = false;
		for (var position = 0; position < sql.Length; position++)
		{
			char symbol = sql[position];
			char next = position + 1 < sql.Length ? sql[position + 1] : '\0';
			if (lineComment)
			{
				builder.Append(symbol);
				if (symbol == '\n')
					lineComment = false;
				continue;
			}

			if (blockComment)
			{
				builder.Append(symbol);
				if (symbol == '*' && next == '/')
				{
					builder.Append(next);
					position++;
					blockComment = false;
				}
				continue;
			}

			if (!quote && !doubleQuote && symbol == '-' && next == '-')
			{
				lineComment = true;
				builder.Append(symbol);
				continue;
			}

			if (!quote && !doubleQuote && symbol == '/' && next == '*')
			{
				blockComment = true;
				builder.Append(symbol);
				continue;
			}

			if (symbol == '\'' && !doubleQuote)
			{
				builder.Append(symbol);
				if (quote && next == '\'')
				{
					builder.Append(next);
					position++;
				}
				else
				{
					quote = !quote;
				}

				continue;
			}

			if (symbol == '"' && !quote)
			{
				doubleQuote = !doubleQuote;
				builder.Append(symbol);
				continue;
			}

			if (symbol == '?' && !quote && !doubleQuote)
				builder.Append(Placeholder(index++));
			else
				builder.Append(symbol);
		}

		return builder.ToString();
	}

	public static readonly SqlDialect Sqlite = new()
	{
		Name = "sqlite",
		QuoteOpen = '"',
		QuoteClose = '"',
		IntegerType = "INTEGER",
		RealType = "REAL",
		TextType = "TEXT",
		BoolType = "INTEGER",
		AutoKey = "INTEGER PRIMARY KEY AUTOINCREMENT",
		Placeholder = index => "$" + index,
		ParameterName = index => "$" + index,
		LastIdSql = "select last_insert_rowid()",
	};

	public static readonly SqlDialect Postgres = new()
	{
		Name = "postgres",
		QuoteOpen = '"',
		QuoteClose = '"',
		IntegerType = "BIGINT",
		RealType = "DOUBLE PRECISION",
		TextType = "TEXT",
		BoolType = "BOOLEAN",
		AutoKey = "BIGSERIAL PRIMARY KEY",
		Placeholder = index => "@p" + (index + 1),
		ParameterName = index => "p" + (index + 1),
		LastIdSql = "select lastval()",
	};

	public static readonly SqlDialect Mysql = new()
	{
		Name = "mysql",
		QuoteOpen = '`',
		QuoteClose = '`',
		IntegerType = "BIGINT",
		RealType = "DOUBLE",
		TextType = "TEXT",
		BoolType = "BOOLEAN",
		AutoKey = "BIGINT PRIMARY KEY AUTO_INCREMENT",
		LastIdSql = "select last_insert_id()",
	};
}

/// <summary>Преобразование URL БД в строку подключения ADO.NET.</summary>
public static class ConnectionStrings
{
	public static string Postgres(string text)
	{
		if (!TryUrl(text, ["postgres", "postgresql"], out Uri? uri))
			return text;
		(string user, string password) = Credentials(uri!);
		string database = Uri.UnescapeDataString(uri!.AbsolutePath.Trim('/'));
		return $"Host={uri.Host};Port={(uri.Port > 0 ? uri.Port : 5432)};Database={database};Username={user};Password={password}";
	}

	public static string Mysql(string text)
	{
		if (!TryUrl(text, ["mysql", "mariadb"], out Uri? uri))
			return text;
		(string user, string password) = Credentials(uri!);
		string database = Uri.UnescapeDataString(uri!.AbsolutePath.Trim('/'));
		return $"Server={uri.Host};Port={(uri.Port > 0 ? uri.Port : 3306)};Database={database};User ID={user};Password={password}";
	}

	public static string Postgres(string host, int port, string database, string user, string password) =>
		$"Host={host};Port={port};Database={database};Username={user};Password={password}";

	public static string Mysql(string host, int port, string database, string user, string password) =>
		$"Server={host};Port={port};Database={database};User ID={user};Password={password}";

	private static bool TryUrl(string text, string[] schemes, out Uri? uri)
	{
		uri = null;
		return Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed) &&
			schemes.Contains(parsed.Scheme) &&
			(uri = parsed) is not null;
	}

	private static (string User, string Password) Credentials(Uri uri)
	{
		string[] parts = uri.UserInfo.Split(':', 2);
		return (Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
	}
}

/// <summary>Общий ADO.NET-движок: параметры `?`, запросы, скаляр, транзакции, async.</summary>
public sealed class DbCore(DbConnection connection, SqlDialect dialect, Interpreter interpreter) : IDisposable
{
	private DbTransaction? _transaction;

	public DbConnection Connection { get; } = connection;
	public SqlDialect Dialect { get; } = dialect;
	public Interpreter Interpreter { get; } = interpreter;

	private DbCommand Command(string sql, KValue args)
	{
		DbCommand command = Connection.CreateCommand();
		command.Transaction = _transaction;
		command.CommandText = Dialect.Rewrite(sql);
		if (args.Ref is KList list)
		{
			for (var index = 0; index < list.Items.Count; index++)
			{
				DbParameter parameter = command.CreateParameter();
				if (Dialect.ParameterName is { } name)
					parameter.ParameterName = name(index);
				parameter.Value = ToDb(list.Items[index]);
				command.Parameters.Add(parameter);
			}
		}

		return command;
	}

	private static bool IsSqlError(Exception exception) => exception is DbException or InvalidOperationException;

	public static object ToDb(KValue value) => value.Kind switch
	{
		ValueKind.Nil or ValueKind.None => DBNull.Value,
		ValueKind.Str => value.AsStr,
		ValueKind.Char => value.AsChar.ToString(),
		ValueKind.Bool => value.AsBool,
		_ when value.IsInteger => value.AsI64,
		_ when value.IsFloat => value.AsF64,
		_ => KernFormat.Value(value),
	};

	public static KValue FromDb(object value) => value switch
	{
		DBNull => KValue.Nil,
		bool flag => KValue.Bool(flag),
		byte number => KValue.U8(number),
		sbyte number => KValue.I8(number),
		short number => KValue.I16(number),
		ushort number => KValue.U16(number),
		int number => KValue.I32(number),
		uint number => KValue.U32(number),
		long number => KValue.I64(number),
		ulong number => KValue.U64(number),
		float number => KValue.F32(number),
		double number => KValue.F64(number),
		decimal number => KValue.F64((double)number),
		string text => KValue.Str(text),
		byte[] bytes => KValue.Str(Convert.ToBase64String(bytes)),
		DateTime date => KValue.Str(date.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
		DateTimeOffset date => KValue.Str(date.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
		Guid guid => KValue.Str(guid.ToString()),
		_ => KValue.Str(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""),
	};

	public KResult<long, string> Exec(string sql, KValue args)
	{
		try
		{
			using DbCommand command = Command(sql, args);
			return KResult<long, string>.Success(command.ExecuteNonQuery());
		}
		catch (Exception exception) when (IsSqlError(exception))
		{
			return KResult<long, string>.Failure(exception.Message);
		}
	}

	public async Task<KResult<long, string>> ExecAsync(string sql, KValue args)
	{
		try
		{
			await using DbCommand command = Command(sql, args);
			return KResult<long, string>.Success(await command.ExecuteNonQueryAsync());
		}
		catch (Exception exception) when (IsSqlError(exception))
		{
			return KResult<long, string>.Failure(exception.Message);
		}
	}

	private KValue Row(DbDataReader reader)
	{
		var row = new KDict(
			Interpreter.Types.Prim(PrimKind.Str),
			Interpreter.Types.Prim(PrimKind.Object));
		for (var index = 0; index < reader.FieldCount; index++)
			row.Set(KValue.Str(reader.GetName(index)), FromDb(reader.GetValue(index)));
		return KValue.Obj(row);
	}

	public KResult<KValue, string> Query(string sql, KValue args)
	{
		try
		{
			using DbCommand command = Command(sql, args);
			using DbDataReader reader = command.ExecuteReader();
			var rows = new List<KValue>();
			while (reader.Read())
				rows.Add(Row(reader));
			return KResult<KValue, string>.Success(KValue.Obj(new KList(null, rows)));
		}
		catch (Exception exception) when (IsSqlError(exception))
		{
			return KResult<KValue, string>.Failure(exception.Message);
		}
	}

	public async Task<KResult<KValue, string>> QueryAsync(string sql, KValue args)
	{
		try
		{
			await using DbCommand command = Command(sql, args);
			await using DbDataReader reader = await command.ExecuteReaderAsync();
			var rows = new List<KValue>();
			while (await reader.ReadAsync())
				rows.Add(Row(reader));
			return KResult<KValue, string>.Success(KValue.Obj(new KList(null, rows)));
		}
		catch (Exception exception) when (IsSqlError(exception))
		{
			return KResult<KValue, string>.Failure(exception.Message);
		}
	}

	/// <summary>Query с настоящим reified-типом <c>Ok&lt;list&lt;dict&lt;str, object&gt;&gt;&gt;</c>.</summary>
	public KValue QueryValue(string sql, KValue args)
	{
		KResult<KValue, string> result = Query(sql, args);
		return result.IsOk
			? KernResults.Ok(Interpreter, RowListType(), result.Ok)
			: KernResults.Err(Interpreter, Interpreter.Types.Prim(PrimKind.Str), KValue.Str(result.Err));
	}

	public async Task<KValue> QueryAsyncValue(string sql, KValue args)
	{
		KResult<KValue, string> result = await QueryAsync(sql, args);
		return result.IsOk
			? KernResults.Ok(Interpreter, RowListType(), result.Ok)
			: KernResults.Err(Interpreter, Interpreter.Types.Prim(PrimKind.Str), KValue.Str(result.Err));
	}

	private KType RowListType() =>
		Interpreter.Types.Builtin(
			BuiltinGenericKind.List,
			Interpreter.Types.Builtin(
				BuiltinGenericKind.Dict,
				Interpreter.Types.Prim(PrimKind.Str),
				Interpreter.Types.Prim(PrimKind.Object)));

	public KResult<KValue, string> Scalar(string sql, KValue args)
	{
		try
		{
			using DbCommand command = Command(sql, args);
			object? value = command.ExecuteScalar();
			return KResult<KValue, string>.Success(value is null ? KValue.Nil : FromDb(value));
		}
		catch (Exception exception) when (IsSqlError(exception))
		{
			return KResult<KValue, string>.Failure(exception.Message);
		}
	}

	public long LastId
	{
		get
		{
			using DbCommand command = Connection.CreateCommand();
			command.Transaction = _transaction;
			command.CommandText = Dialect.LastIdSql;
			return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
		}
	}

	public void Begin() => _transaction ??= Connection.BeginTransaction();

	public void Commit()
	{
		_transaction?.Commit();
		_transaction?.Dispose();
		_transaction = null;
	}

	public void Rollback()
	{
		_transaction?.Rollback();
		_transaction?.Dispose();
		_transaction = null;
	}

	public void Dispose()
	{
		_transaction?.Dispose();
		Connection.Dispose();
	}
}
