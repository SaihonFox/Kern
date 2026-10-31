using System.Globalization;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Serialization.Yaml;

/// <summary>
/// YAML (подмножество для конфигов): вложенные словари и списки по отступам, `- элемент`, скаляры (число/bool/null/строка,
/// кавычки), потоковые `[a, b]` и `{k: v}`, блочные строки `|` и `>`, комментарии `#`. Якоря, теги и несколько документов не поддержаны.
/// Результат — те же динамические значения, что у json.parse.
/// </summary>
[KernType("yaml")]
public static class YamlApi
{
	[KernStatic] public static KResult<object, string> Parse(string text)
	{
		try { return KResult<object, string>.Ok(new Reader(text).Document()); }
		catch (FormatException e) { return Kv.Err<object>(e.Message); }
	}

	[KernStatic] public static string ToStr(KValue value)
	{
		var sb = new StringBuilder();
		Write(sb, value, 0, inList: false);
		return sb.ToString();
	}

	// ---------- запись ----------

	private static void Write(StringBuilder sb, KValue v, int indent, bool inList)
	{
		string pad = new(' ', indent);
		switch (v.Ref)
		{
			case KDictObject d when d.Items.Count > 0:
				bool first = true;
				foreach (var (k, val) in d.Items)
				{
					if (!(inList && first)) sb.Append(pad);
					first = false;
					sb.Append(Scalar(k)).Append(':');
					WriteValue(sb, val, indent);
				}
				break;
			case KListObject l when l.Items.Count > 0:
				bool firstItem = true;
				foreach (var item in l.Items)
				{
					if (!(inList && firstItem)) sb.Append(pad);
					firstItem = false;
					sb.Append('-');
					WriteItem(sb, item, indent);
				}
				break;
			default:
				sb.Append(pad).Append(Scalar(v)).Append('\n');
				break;
		}
	}

	private static bool IsBlock(KValue v) => v.Ref is KDictObject { Items.Count: > 0 } or KListObject { Items.Count: > 0 };

	private static void WriteValue(StringBuilder sb, KValue v, int indent)
	{
		if (IsBlock(v)) { sb.Append('\n'); Write(sb, v, indent + 2, false); }
		else sb.Append(' ').Append(Scalar(v)).Append('\n');
	}

	private static void WriteItem(StringBuilder sb, KValue v, int indent)
	{
		if (IsBlock(v)) { sb.Append(' '); Write(sb, v, indent + 2, inList: true); }
		else sb.Append(' ').Append(Scalar(v)).Append('\n');
	}

	private static string Scalar(KValue v)
	{
		switch (v.Ref)
		{
			case KDictObject: return "{}";
			case KListObject: return "[]";
		}
		if (v.IsNilOrNone) return "null";
		if (v.Kind == ValueKind.Str)
		{
			string s = v.AsStr;
			bool plain = s.Length > 0 && s.Trim() == s && s.IndexOfAny([':', '#', '\n', '"', '\'', '[', ']', '{', '}', ',']) < 0
				&& s[0] is not ('-' or '&' or '*' or '!' or '|' or '>' or '%' or '@')
				&& !LooksLikeScalar(s);
			return plain ? s : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
		}
		return ValueOps.ToStr(v);
	}

	private static bool LooksLikeScalar(string s) =>
		s is "null" or "~" or "true" or "false" or "yes" or "no" || double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out _);

	// ---------- чтение ----------

	private sealed class Reader
	{
		private readonly List<(int Indent, string Text)> _lines = [];
		private int _p;

		public Reader(string text)
		{
			foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
			{
				if (raw.TrimStart().StartsWith('#') || raw.Trim().Length == 0 || raw.Trim() is "---" or "...") { _lines.Add((-1, raw)); continue; }
				int indent = raw.Length - raw.TrimStart(' ').Length;
				_lines.Add((indent, StripComment(raw.TrimStart(' ')).TrimEnd()));
			}
		}

		private static string StripComment(string s)
		{
			bool sq = false, dq = false;
			for (int i = 0; i < s.Length; i++)
			{
				if (s[i] == '\'' && !dq) sq = !sq;
				else if (s[i] == '"' && !sq && (i == 0 || s[i - 1] != '\\')) dq = !dq;
				else if (s[i] == '#' && !sq && !dq && (i == 0 || s[i - 1] == ' ')) return s[..i];
			}
			return s;
		}

		private void SkipBlank() { while (_p < _lines.Count && _lines[_p].Indent < 0) _p++; }

		public KValue Document()
		{
			SkipBlank();
			if (_p >= _lines.Count) return KValue.Nil;
			var v = Block(_lines[_p].Indent);
			SkipBlank();
			if (_p < _lines.Count) throw new FormatException($"yaml: неожиданный отступ в строке {_p + 1}");
			return v;
		}

		private KValue Block(int indent)
		{
			SkipBlank();
			var (ind, text) = _lines[_p];
			if (text.StartsWith("- ") || text == "-") return List(ind);
			if (FindKeyColon(text) >= 0) return Map(ind);
			_p++;
			return Inline(text);
		}

		private KValue List(int indent)
		{
			var items = new List<KValue>();
			while (true)
			{
				SkipBlank();
				if (_p >= _lines.Count || _lines[_p].Indent != indent || !(_lines[_p].Text.StartsWith("- ") || _lines[_p].Text == "-")) break;
				string rest = _lines[_p].Text.Length > 2 ? _lines[_p].Text[2..].TrimStart() : "";
				if (rest.Length == 0)
				{
					_p++;
					SkipBlank();
					items.Add(_p < _lines.Count && _lines[_p].Indent > indent ? Block(_lines[_p].Indent) : KValue.Nil);
				}
				else if (FindKeyColon(rest) >= 0 && !rest.StartsWith('[') && !rest.StartsWith('{') && !rest.StartsWith('"'))
				{
					// `- key: v` — словарь, ключи которого выровнены по первому
					int inner = indent + (_lines[_p].Text.Length - rest.Length);
					_lines[_p] = (inner, rest);
					items.Add(Map(inner));
				}
				else { _p++; items.Add(Inline(rest)); }
			}
			return KValue.Obj(new KListObject(TypeFactory.Object, items));
		}

		private KValue Map(int indent)
		{
			var d = new KDictObject(TypeFactory.Str, TypeFactory.Object);
			while (true)
			{
				SkipBlank();
				if (_p >= _lines.Count || _lines[_p].Indent != indent) break;
				string text = _lines[_p].Text;
				int c = FindKeyColon(text);
				if (c < 0) throw new FormatException($"yaml: ожидалось `ключ: значение` в строке {_p + 1}");
				string key = Unquote(text[..c].Trim());
				string rest = text[(c + 1)..].Trim();
				_p++;
				KValue value;
				if (rest is "|" or ">" or "|-" or ">-") value = BlockScalar(indent, folded: rest[0] == '>', strip: rest.EndsWith('-'));
				else if (rest.Length > 0) value = Inline(rest);
				else
				{
					SkipBlank();
					if (_p < _lines.Count && (_lines[_p].Indent > indent || (_lines[_p].Indent == indent && _lines[_p].Text.StartsWith("- "))))
						value = Block(_lines[_p].Indent);
					else value = KValue.Nil;
				}
				d.Items[KValue.Str(key)] = value;
			}
			return KValue.Obj(d);
		}

		private KValue BlockScalar(int parentIndent, bool folded, bool strip)
		{
			var parts = new List<string>();
			while (_p < _lines.Count && (_lines[_p].Indent < 0 || _lines[_p].Indent > parentIndent))
			{
				parts.Add(_lines[_p].Indent < 0 ? "" : _lines[_p].Text);
				_p++;
			}
			string s = string.Join(folded ? " " : "\n", parts).TrimEnd('\n', ' ');
			return KValue.Str(strip ? s : s + "\n");
		}

		/// <summary>Индекс `:` разделителя ключа (вне кавычек, за ним пробел или конец).</summary>
		private static int FindKeyColon(string s)
		{
			bool sq = false, dq = false;
			for (int i = 0; i < s.Length; i++)
			{
				char ch = s[i];
				if (ch == '\'' && !dq) sq = !sq;
				else if (ch == '"' && !sq) dq = !dq;
				else if (ch == ':' && !sq && !dq && (i + 1 == s.Length || s[i + 1] == ' ')) return i;
				else if ((ch == '[' || ch == '{') && i == 0 && !sq && !dq) return -1;
			}
			return -1;
		}

		private static string Unquote(string s) =>
			s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\''))
				? (s[0] == '"' ? s[1..^1].Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\") : s[1..^1].Replace("''", "'"))
				: s;

		private KValue Inline(string s)
		{
			s = s.Trim();
			if (s.StartsWith('[') || s.StartsWith('{'))
			{
				int pos = 0;
				return Flow(s, ref pos);
			}
			return ScalarValue(s);
		}

		private static KValue ScalarValue(string s)
		{
			if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'')) return KValue.Str(Unquote(s));
			switch (s)
			{
				case "null" or "~" or "": return KValue.Nil;
				case "true" or "True": return KValue.True;
				case "false" or "False": return KValue.False;
			}
			if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return KValue.I64(l);
			if (s.Contains('.') && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return KValue.F64(d);
			return KValue.Str(s);
		}

		private KValue Flow(string s, ref int i)
		{
			while (i < s.Length && s[i] == ' ') i++;
			if (i >= s.Length) throw new FormatException("yaml: неожиданный конец потокового значения");
			if (s[i] == '[')
			{
				i++;
				var items = new List<KValue>();
				while (true)
				{
					while (i < s.Length && s[i] == ' ') i++;
					if (i >= s.Length) throw new FormatException("yaml: нет закрывающей ']'");
					if (s[i] == ']') { i++; break; }
					items.Add(Flow(s, ref i));
					while (i < s.Length && s[i] == ' ') i++;
					if (i < s.Length && s[i] == ',') i++;
				}
				return KValue.Obj(new KListObject(TypeFactory.Object, items));
			}
			if (s[i] == '{')
			{
				i++;
				var d = new KDictObject(TypeFactory.Str, TypeFactory.Object);
				while (true)
				{
					while (i < s.Length && s[i] == ' ') i++;
					if (i >= s.Length) throw new FormatException("yaml: нет закрывающей '}'");
					if (s[i] == '}') { i++; break; }
					string key = Unquote(Token(s, ref i, ':').Trim());
					if (i < s.Length && s[i] == ':') i++;
					d.Items[KValue.Str(key)] = Flow(s, ref i);
					while (i < s.Length && s[i] == ' ') i++;
					if (i < s.Length && s[i] == ',') i++;
				}
				return KValue.Obj(d);
			}
			return ScalarValue(Token(s, ref i, ',', ']', '}').Trim());
		}

		private static string Token(string s, ref int i, params char[] stops)
		{
			int start = i;
			bool sq = false, dq = false;
			while (i < s.Length)
			{
				char ch = s[i];
				if (ch == '\'' && !dq) sq = !sq;
				else if (ch == '"' && !sq) dq = !dq;
				else if (!sq && !dq && Array.IndexOf(stops, ch) >= 0) break;
				i++;
			}
			return s[start..i];
		}
	}
}
