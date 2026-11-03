using System.Globalization;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Bytecode;
using Kern.Binding.Formatting;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Toml;

/// <summary>
/// <c>kern.toml</c> (spec §16): подмножество TOML для конфигов — таблицы
/// <c>[a.b]</c>, массивы таблиц <c>[[a]]</c>, inline-таблицы <c>{ … }</c>,
/// скаляры, массивы (в том числе многострочные), строки в кавычках и комментарии <c>#</c>.
/// Даты и время читаются как ISO-строки (отдельного типа в Kern нет).
/// </summary>
[KernModule("kern.toml")]
public static class TomlApi
{
	/// <summary><c>toml.parse(text)</c>: словарь верхнего уровня.</summary>
	[KernMember]
	[KernReturn("result<dict<str, object>, str>")]
	public static KValue Parse(Interpreter interpreter, string text)
	{
		KType str = interpreter.Types.Prim(PrimKind.Str);
		KType obj = interpreter.Types.Prim(PrimKind.Object);
		var root = new KDict(str, obj);
		KDict current = root;
		string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		for (var index = 0; index < lines.Length; index++)
		{
			string line = StripComment(lines[index]).Trim();
			while (NeedsContinuation(line) && index + 1 < lines.Length)
				line += " " + StripComment(lines[++index]).Trim();
			if (line.Length == 0)
				continue;
			if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
			{
				string path = line[2..^2];
				int dot = path.LastIndexOf('.');
				KDict parent = dot < 0 ? root : EnsureTable(root, path[..dot], str, obj);
				KValue name = KValue.Str(Unquote((dot < 0 ? path : path[(dot + 1)..]).Trim()));
				if (!parent.TryGet(name, out KValue existing) || existing.Ref is not KList list)
				{
					list = new KList(null, []);
					parent.Set(name, KValue.Obj(list));
				}

				var element = new KDict(str, obj);
				list.Add(KValue.Obj(element));
				current = element;
				continue;
			}

			if (line[0] == '[' && line[^1] == ']')
			{
				current = EnsureTable(root, line[1..^1], str, obj);
				continue;
			}

			int equals = FindEquals(line);
			if (equals <= 0)
				return Failure(interpreter, $"toml: строка {index + 1}: ожидалось key = value");
			string key = Unquote(line[..equals].Trim());
			string raw = line[(equals + 1)..].Trim();
			try
			{
				SetDotted(current, key, Value(raw, interpreter), str, obj);
			}
			catch (FormatException error)
			{
				return Failure(interpreter, $"toml: строка {index + 1}: {error.Message}");
			}
		}

		return KernResults.Ok(
			interpreter,
			interpreter.Types.Builtin(BuiltinGenericKind.Dict,
				interpreter.Types.Prim(PrimKind.Str),
				interpreter.Types.Prim(PrimKind.Object)),
			KValue.Obj(root));
	}

	/// <summary><c>toml.to_str(value)</c>: записать словарь как TOML (без дат).</summary>
	[KernMember]
	[KernName("to_str")]
	public static string ToStr(KValue value)
	{
		if (value.Ref is not KDict root)
			throw new KernPanic($"toml.to_str: ожидался dict, получено {value.TypeName}");
		var builder = new StringBuilder();
		WriteTable(builder, root, "");
		return builder.ToString();
	}

	private static void WriteTable(StringBuilder builder, KDict table, string prefix)
	{
		foreach (var (key, item) in table.Items)
		{
			if (item.Ref is KDict || IsTableArray(item))
				continue;
			builder.Append(key.AsStr).Append(" = ").Append(Scalar(item)).Append('\n');
		}

		foreach (var (key, item) in table.Items)
		{
			if (item.Ref is not KDict nested)
				continue;
			string name = prefix.Length == 0 ? key.AsStr : prefix + "." + key.AsStr;
			builder.Append('\n').Append('[').Append(name).Append("]\n");
			WriteTable(builder, nested, name);
		}

		foreach (var (key, item) in table.Items)
		{
			if (!IsTableArray(item))
				continue;
			string name = prefix.Length == 0 ? key.AsStr : prefix + "." + key.AsStr;
			foreach (KValue element in ((KList)item.Ref!).Items)
			{
				if (element.Ref is not KDict nested)
					continue;
				builder.Append('\n').Append("[[").Append(name).Append("]]\n");
				WriteTable(builder, nested, name);
			}
		}
	}

	private static bool IsTableArray(KValue value) =>
		value.Ref is KList list && list.Count > 0 && list.Items.All(item => item.Ref is KDict);

	private static string Scalar(KValue value) => value.Kind switch
	{
		ValueKind.Str => Quote(value.AsStr),
		ValueKind.Bool => value.AsBool ? "true" : "false",
		ValueKind.I8 or ValueKind.I16 or ValueKind.I32 or ValueKind.I64 => value.AsI64.ToString(CultureInfo.InvariantCulture),
		ValueKind.U8 or ValueKind.U16 or ValueKind.U32 or ValueKind.U64 => value.AsU64.ToString(CultureInfo.InvariantCulture),
		ValueKind.F16 or ValueKind.F32 or ValueKind.F64 => value.AsF64.ToString("R", CultureInfo.InvariantCulture),
		ValueKind.Ref when value.Ref is KList list =>
			"[" + string.Join(", ", list.Items.Select(Scalar)) + "]",
		_ => throw new KernPanic($"toml.to_str: неподдерживаемое значение {value.TypeName}"),
	};

	private static KValue Value(string raw, Interpreter interpreter)
	{
		if (raw.StartsWith('"') && raw.EndsWith('"') && raw.Length >= 2)
			return KValue.Str(Unescape(raw[1..^1]));
		if (raw.StartsWith('\'') && raw.EndsWith('\'') && raw.Length >= 2)
			return KValue.Str(raw[1..^1]);
		if (raw == "true")
			return KValue.Bool(true);
		if (raw == "false")
			return KValue.Bool(false);
		if (raw.StartsWith('{') && raw.EndsWith('}'))
		{
			var inline = new KDict();
			string body = raw[1..^1].Trim();
			if (body.Length > 0)
				foreach (string part in SplitTopLevel(body))
				{
					string item = part.Trim();
					if (item.Length == 0) continue;
					int equals = FindEquals(item);
					if (equals <= 0)
						throw new FormatException($"ожидалось key = value в inline-таблице, получено '{item}'");
					SetDotted(inline, Unquote(item[..equals].Trim()), Value(item[(equals + 1)..].Trim(), interpreter),
						interpreter.Types.Prim(PrimKind.Str), interpreter.Types.Prim(PrimKind.Object));
				}

			return KValue.Obj(inline);
		}

		if (raw.StartsWith('[') && raw.EndsWith(']'))
		{
			var items = new List<KValue>();
			string inner = raw[1..^1].Trim();
			if (inner.Length > 0)
				foreach (string part in SplitTopLevel(inner))
				{
					string item = part.Trim();
					if (item.Length > 0) items.Add(Value(item, interpreter));
				}

			return KValue.Obj(new KList(null, items));
		}

		if (LooksLikeDateTime(raw))
			return KValue.Str(raw);
		string number = raw.Replace("_", "", StringComparison.Ordinal);
		if (number is "inf" or "+inf")
			return KValue.F64(double.PositiveInfinity);
		if (number == "-inf")
			return KValue.F64(double.NegativeInfinity);
		if (number is "nan" or "+nan" or "-nan")
			return KValue.F64(double.NaN);
		if (number.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
			long.TryParse(number[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
			return KValue.I64(hex);
		if (number.StartsWith("0o", StringComparison.OrdinalIgnoreCase) &&
			TryParseBase(number[2..], 8, out long octal))
			return KValue.I64(octal);
		if (number.StartsWith("0b", StringComparison.OrdinalIgnoreCase) &&
			TryParseBase(number[2..], 2, out long binary))
			return KValue.I64(binary);
		if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
			return KValue.I64(integer);
		if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
			return KValue.F64(real);
		throw new FormatException($"не удалось разобрать значение '{raw}'");
	}

	private static bool TryParseBase(string text, int radix, out long value)
	{
		value = 0;
		if (text.Length == 0) return false;
		foreach (char symbol in text)
		{
			int digit = symbol switch
			{
				>= '0' and <= '9' => symbol - '0',
				>= 'a' and <= 'f' => symbol - 'a' + 10,
				>= 'A' and <= 'F' => symbol - 'A' + 10,
				_ => -1,
			};
			if (digit < 0 || digit >= radix) return false;
			value = value * radix + digit;
		}

		return true;
	}

	private static bool LooksLikeDateTime(string raw)
	{
		if (raw.Length < 8) return false;
		if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return true;
		if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return true;
		return TimeOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
	}

	private static IEnumerable<string> SplitTopLevel(string text)
	{
		var start = 0;
		var quote = '\0';
		var depth = 0;
		for (var index = 0; index < text.Length; index++)
		{
			char symbol = text[index];
			if (quote != '\0')
			{
				if (symbol == quote && (index == 0 || text[index - 1] != '\\'))
					quote = '\0';
				continue;
			}

			if (symbol is '"' or '\'')
				quote = symbol;
			else if (symbol is '[' or '{')
				depth++;
			else if (symbol is ']' or '}')
				depth--;
			else if (symbol == ',' && depth == 0)
			{
				yield return text[start..index];
				start = index + 1;
			}
		}

		yield return text[start..];
	}

	private static bool NeedsContinuation(string line)
	{
		var quote = '\0';
		var depth = 0;
		for (var index = 0; index < line.Length; index++)
		{
			char symbol = line[index];
			if (quote != '\0')
			{
				if (symbol == quote && (index == 0 || line[index - 1] != '\\'))
					quote = '\0';
				continue;
			}

			if (symbol is '"' or '\'')
				quote = symbol;
			else if (symbol is '[' or '{')
				depth++;
			else if (symbol is ']' or '}')
				depth--;
		}

		return quote != '\0' || depth > 0;
	}

	private static KDict EnsureTable(KDict root, string path, KType strType, KType objType)
	{
		KDict table = root;
		foreach (string part in path.Split('.'))
		{
			string name = Unquote(part.Trim());
			if (name.Length == 0) continue;
			if (table.TryGet(KValue.Str(name), out KValue existing) && existing.Ref is KDict nested)
				table = nested;
			else
			{
				var child = new KDict(strType, objType);
				table.Set(KValue.Str(name), KValue.Obj(child));
				table = child;
			}
		}

		return table;
	}

	private static void SetDotted(KDict table, string key, KValue value, KType strType, KType objType)
	{
		string[] parts = key.Split('.');
		for (var index = 0; index < parts.Length - 1; index++)
		{
			KValue name = KValue.Str(parts[index]);
			if (table.TryGet(name, out KValue existing) && existing.Ref is KDict nested)
				table = nested;
			else
			{
				var child = new KDict(strType, objType);
				table.Set(name, KValue.Obj(child));
				table = child;
			}
		}

		table.Set(KValue.Str(parts[^1]), value);
	}

	private static int FindEquals(string line)
	{
		var quote = '\0';
		for (var index = 0; index < line.Length; index++)
		{
			char symbol = line[index];
			if (quote != '\0')
			{
				if (symbol == quote && (index == 0 || line[index - 1] != '\\'))
					quote = '\0';
				continue;
			}

			if (symbol is '"' or '\'')
				quote = symbol;
			else if (symbol == '=')
				return index;
		}

		return -1;
	}

	private static string StripComment(string line)
	{
		var quote = '\0';
		for (var index = 0; index < line.Length; index++)
		{
			char symbol = line[index];
			if (quote != '\0')
			{
				if (symbol == quote && (index == 0 || line[index - 1] != '\\'))
					quote = '\0';
				continue;
			}

			if (symbol is '"' or '\'')
				quote = symbol;
			else if (symbol == '#')
				return line[..index];
		}

		return line;
	}

	private static string Unquote(string text) =>
		text.Length >= 2 && text[0] == text[^1] && text[0] is '"' or '\''
			? text[1..^1]
			: text;

	private static string Unescape(string text)
	{
		var builder = new StringBuilder(text.Length);
		for (var index = 0; index < text.Length; index++)
		{
			char symbol = text[index];
			if (symbol == '\\' && index + 1 < text.Length)
			{
				char next = text[++index];
				builder.Append(next switch
				{
					'n' => '\n',
					't' => '\t',
					'r' => '\r',
					'"' => '"',
					'\\' => '\\',
					_ => next,
				});
				continue;
			}

			builder.Append(symbol);
		}

		return builder.ToString();
	}

	private static string Quote(string text) =>
		"\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"")
			.Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r") + "\"";

	private static KValue Failure(Interpreter interpreter, string message) =>
		KernResults.Err(interpreter, interpreter.Types.Prim(PrimKind.Str), KValue.Str(message));
}
