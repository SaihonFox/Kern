using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Marshalling;
using Kern.Binding.Values;
using Kern.Vm.Values;
using VmKList = Kern.Vm.Values.KList;

namespace Kern.Runtime.Csv;

/// <summary>
/// <c>kern.csv</c> (spec §16, раунд 5): разбор и запись CSV по RFC 4180.
/// Кавычки, удвоенные кавычки внутри поля, запятые и переводы строк в кавычках.
/// Ошибки формата — <c>Err</c> с русским текстом.
/// </summary>
[KernModule("kern.csv")]
public static class CsvApi
{
	/// <summary><c>csv.parse(text)</c>: строки таблицы.</summary>
	[KernMember]
	public static KResult<KList<KList<string>>, string> Parse(string text)
	{
		try
		{
			List<List<string>> rows = ParseRows(text);
			return KResult<KList<KList<string>>, string>.Success(ListOf(rows.Select(ListOf)));
		}
		catch (FormatException exception)
		{
			return KResult<KList<KList<string>>, string>.Failure($"csv: {exception.Message}");
		}
	}

	/// <summary><c>csv.parse_dicts(text)</c>: первая строка — заголовки, остальные — записи.</summary>
	[KernMember]
	public static KResult<KList<KDict<string, string>>, string> ParseDicts(string text)
	{
		try
		{
			List<List<string>> rows = ParseRows(text);
			if (rows.Count == 0)
				return KResult<KList<KDict<string, string>>, string>.Success(ListOf<KDict<string, string>>([]));

			List<string> headers = rows[0];
			var records = new List<KDict<string, string>>();
			for (var index = 1; index < rows.Count; index++)
			{
				List<string> row = rows[index];
				if (row.Count == 1 && row[0].Length == 0)
					continue;
				if (row.Count != headers.Count)
					throw new FormatException(
						$"строка {index + 1}: колонок {row.Count}, ожидалось {headers.Count}");

				var raw = new KDict();
				for (var column = 0; column < headers.Count; column++)
					raw.Set(KValue.Str(headers[column]), KValue.Str(row[column]));
				records.Add(new KDict<string, string>(KValue.Obj(raw)));
			}

			return KResult<KList<KDict<string, string>>, string>.Success(ListOf(records));
		}
		catch (FormatException exception)
		{
			return KResult<KList<KDict<string, string>>, string>.Failure($"csv: {exception.Message}");
		}
	}

	/// <summary><c>csv.to_str(rows)</c>: таблица в CSV (CRLF между строками).</summary>
	[KernMember]
	public static string ToStr(KList<KList<string>> rows)
	{
		var builder = new StringBuilder();
		foreach (KList<string> row in rows)
		{
			if (builder.Length > 0)
				builder.Append("\r\n");
			var first = true;
			foreach (string field in row)
			{
				if (!first)
					builder.Append(',');
				first = false;
				builder.Append(Quote(field));
			}
		}

		return builder.ToString();
	}

	private static string Quote(string field) =>
		field.IndexOfAny([',', '"', '\n', '\r']) < 0
			? field
			: "\"" + field.Replace("\"", "\"\"") + "\"";

	private static KList<T> ListOf<T>(IEnumerable<T> items) =>
		new(KValue.Obj(new VmKList(
			null,
			[.. items.Select(item => KMarshal.From(item))])));

	private static List<List<string>> ParseRows(string text)
	{
		var rows = new List<List<string>>();
		var row = new List<string>();
		var field = new StringBuilder();
		var quoted = false;
		var index = 0;
		while (index < text.Length)
		{
			char current = text[index];
			if (quoted)
			{
				if (current == '"')
				{
					if (index + 1 < text.Length && text[index + 1] == '"')
					{
						field.Append('"');
						index += 2;
						continue;
					}

					quoted = false;
					index++;
					continue;
				}

				field.Append(current);
				index++;
				continue;
			}

			switch (current)
			{
				case '"':
					quoted = true;
					index++;
					break;
				case ',':
					row.Add(field.ToString());
					field.Clear();
					index++;
					break;
				case '\r':
				case '\n':
					if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
						index++;
					row.Add(field.ToString());
					field.Clear();
					rows.Add(row);
					row = [];
					index++;
					break;
				default:
					field.Append(current);
					index++;
					break;
			}
		}

		if (quoted)
			throw new FormatException("незакрытая кавычка");
		if (field.Length > 0 || row.Count > 0)
		{
			row.Add(field.ToString());
			rows.Add(row);
		}

		return rows;
	}
}
