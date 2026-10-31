using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Serialization.Csv;

/// <summary>
/// CSV (RFC 4180): кавычки, экранирование "", переводы строк внутри поля.
/// <c>csv.parse(text)</c> → list&lt;list&lt;str&gt;&gt;; <c>csv.parse_dicts(text)</c> — первая строка = заголовки.
/// </summary>
[KernType("csv")]
public static class CsvApi
{
	private static List<List<string>> ParseRows(string text, char sep)
	{
		var rows = new List<List<string>>();
		var row = new List<string>();
		var field = new StringBuilder();
		bool quoted = false, any = false;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (quoted)
			{
				if (c == '"')
				{
					if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
					else quoted = false;
				}
				else field.Append(c);
				continue;
			}
			if (c == '"') { quoted = true; any = true; }
			else if (c == sep) { row.Add(field.ToString()); field.Clear(); any = true; }
			else if (c is '\r' or '\n')
			{
				if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
				if (any || field.Length > 0) { row.Add(field.ToString()); rows.Add(row); }
				row = []; field.Clear(); any = false;
			}
			else { field.Append(c); any = true; }
		}
		if (quoted) throw new Kern.Vm.Execution.KernPanic("csv: незакрытая кавычка");
		if (any || field.Length > 0) { row.Add(field.ToString()); rows.Add(row); }
		return rows;
	}

	[KernStatic] public static KResult<KList<KList<string>>, string> Parse(string text, string separator = ",")
	{
		try
		{
			var rows = ParseRows(text, separator.Length > 0 ? separator[0] : ',');
			var list = Kv.List<KList<string>>(rows.Select(r => KValue.Obj(Kv.Strs(r).Inner)));
			return Kv.Ok<KList<KList<string>>>(KValue.Obj(list.Inner));
		}
		catch (Kern.Vm.Execution.KernPanic e) { return Kv.Err<KList<KList<string>>>(e.Message); }
	}

	[KernStatic] public static KResult<KList<KDict<string, string>>, string> ParseDicts(string text, string separator = ",")
	{
		try
		{
			var rows = ParseRows(text, separator.Length > 0 ? separator[0] : ',');
			var items = new List<KValue>();
			if (rows.Count > 0)
			{
				var head = rows[0];
				foreach (var r in rows.Skip(1))
				{
					var d = new KDictObject(TypeFactory.Str, TypeFactory.Str);
					for (int i = 0; i < head.Count; i++) d.Items[KValue.Str(head[i])] = KValue.Str(i < r.Count ? r[i] : "");
					items.Add(KValue.Obj(d));
				}
			}
			return Kv.Ok<KList<KDict<string, string>>>(KValue.Obj(Kv.List<KDict<string, string>>(items).Inner));
		}
		catch (Kern.Vm.Execution.KernPanic e) { return Kv.Err<KList<KDict<string, string>>>(e.Message); }
	}

	private static string Quote(string s, char sep) =>
		s.IndexOfAny([sep, '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

	[KernStatic] public static string ToStr(KList<KList<string>> rows, string separator = ",")
	{
		char sep = separator.Length > 0 ? separator[0] : ',';
		var sb = new StringBuilder();
		foreach (var row in rows.Items)
		{
			var cells = ((KListObject)row.Ref!).Items.Select(c => Quote(c.AsStr, sep));
			sb.AppendJoin(sep, cells).Append('\n');
		}
		return sb.ToString();
	}
}
