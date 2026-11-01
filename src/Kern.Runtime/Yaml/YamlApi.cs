using System.Globalization;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Yaml;

/// <summary>
/// <c>kern.yaml</c> (spec §16, раунд 5): подмножество YAML для конфигов — отображения,
/// последовательности (блочные и <c>[a, b]</c>), скаляры. Комментарии <c>#</c> и пустые
/// строки игнорируются. Документ представлен <c>dict&lt;str, object&gt;</c> и
/// <c>list&lt;object&gt;</c>; <c>to_str</c> пишет обратно в блочном стиле.
/// </summary>
[KernModule("kern.yaml")]
public static class YamlApi
{
	/// <summary><c>yaml.parse(text)</c>: разобрать документ в значение Kern.</summary>
	[KernMember]
	public static KResult<KValue, string> Parse(string text, Interpreter interpreter)
	{
		try
		{
			var parser = new Parser(text, interpreter.Types);
			return KResult<KValue, string>.Success(parser.ParseDocument());
		}
		catch (FormatException exception)
		{
			return KResult<KValue, string>.Failure($"yaml: {exception.Message}");
		}
	}

	/// <summary><c>yaml.to_str(value)</c>: записать словарь/список/скаляр в YAML.</summary>
	[KernMember]
	public static string ToStr(KValue value)
	{
		var builder = new StringBuilder();
		Write(value, builder, 0, inline: false);
		return builder.ToString().TrimEnd('\n');
	}

	private static void Write(KValue value, StringBuilder builder, int indent, bool inline)
	{
		switch (value.Kind)
		{
			case ValueKind.Ref when value.Ref is KDict dict:
				if (inline)
					throw new FormatException("yaml.to_str: словарь нельзя записать в строку");
				foreach (var (key, item) in dict.Items)
				{
					builder.Append(' ', indent).Append(Key(key)).Append(':');
					if (item.Kind == ValueKind.Ref && item.Ref is KDict or KList)
					{
						builder.Append('\n');
						Write(item, builder, indent + 2, inline: false);
					}
					else
					{
						builder.Append(' ');
						Write(item, builder, 0, inline: true);
						builder.Append('\n');
					}
				}

				break;
			case ValueKind.Ref when value.Ref is KList list:
				if (inline)
				{
					builder.Append('[').Append(string.Join(", ", list.Items.Select(Scalar))).Append(']');
					break;
				}

				foreach (KValue item in list.Items)
				{
					builder.Append(' ', indent).Append("- ");
					if (item.Kind == ValueKind.Ref && item.Ref is KDict or KList)
					{
						builder.Append('\n');
						Write(item, builder, indent + 2, inline: false);
					}
					else
					{
						Write(item, builder, 0, inline: true);
						builder.Append('\n');
					}
				}

				break;
			default:
				builder.Append(Scalar(value));
				if (!inline)
					builder.Append('\n');
				break;
		}
	}

	private static string Key(KValue key) => key.Kind switch
	{
		ValueKind.Str => key.AsStr,
		ValueKind.Char => key.AsChar.ToString(),
		_ => Scalar(key),
	};

	private static string Scalar(KValue value) => value.Kind switch
	{
		ValueKind.None or ValueKind.Nil => "null",
		ValueKind.Bool => value.AsBool ? "true" : "false",
		ValueKind.Char => value.AsChar.ToString(),
		ValueKind.Str => QuoteIfNeeded(value.AsStr),
		ValueKind.I8 or ValueKind.I16 or ValueKind.I32 or ValueKind.I64 => value.AsI64.ToString(CultureInfo.InvariantCulture),
		ValueKind.U8 or ValueKind.U16 or ValueKind.U32 or ValueKind.U64 => value.AsU64.ToString(CultureInfo.InvariantCulture),
		ValueKind.F16 or ValueKind.F32 or ValueKind.F64 => value.AsF64.ToString("R", CultureInfo.InvariantCulture),
		_ => throw new FormatException($"yaml.to_str: значение типа {value.Kind} не поддержано"),
	};

	private static string QuoteIfNeeded(string text)
	{
		if (text.Length == 0)
			return "''";
		bool plain = text[0] is not (' ' or '#' or '-' or '?' or ':' or '[' or ']' or '{' or '}' or '"' or '\'') &&
			!text.Contains(':') && !text.Contains('#') && text.Trim() == text;
		return plain ? text : "'" + text.Replace("'", "''") + "'";
	}

	private sealed class Parser(string text, TypeFactory factory)
	{
		private readonly List<Line> _lines = Build(text);
		private int _position;

		public KValue ParseDocument()
		{
			if (_lines.Count == 0)
				return KValue.Nil;

			KValue value = ParseBlock(_lines[0].Indent);
			if (_position < _lines.Count)
				throw new FormatException($"строка {_lines[_position].Number}: лишний отступ");
			return value;
		}

		private KValue ParseBlock(int indent)
		{
			if (_position >= _lines.Count)
				return KValue.Nil;
			Line line = _lines[_position];
			return line.Text.StartsWith("- ", StringComparison.Ordinal) || line.Text == "-"
				? ParseSequence(indent)
				: ParseMapping(indent);
		}

		private KValue ParseSequence(int indent)
		{
			var list = new KList(factory.Prim(PrimKind.Object));
			while (_position < _lines.Count)
			{
				Line line = _lines[_position];
				if (line.Indent < indent)
					break;
				if (line.Indent != indent)
					throw new FormatException($"строка {line.Number}: неверный отступ");
				if (!line.Text.StartsWith("-", StringComparison.Ordinal))
					break;

				string rest = line.Text.Length == 1 ? "" : line.Text[2..].TrimStart();
				_position++;
				if (rest.Length == 0)
				{
					list.Add(_position < _lines.Count && _lines[_position].Indent > indent
						? ParseBlock(_lines[_position].Indent)
						: KValue.Nil);
					continue;
				}

				int colon = rest.IndexOf(':');
				if (colon > 0 && !rest.TrimStart().StartsWith('[') &&
					(colon + 1 == rest.Length || rest[colon + 1] == ' '))
				{
					// "- key: value" — элемент-словарь; остальные ключи идут с отступом.
					var item = new KDict(factory.Prim(PrimKind.Str), factory.Prim(PrimKind.Object));
					item.Set(KValue.Str(rest[..colon].Trim()), rest[(colon + 1)..].Trim().Length == 0
						? KValue.Nil
						: ParseScalar(rest[(colon + 1)..].Trim()));
					while (_position < _lines.Count && _lines[_position].Indent > indent)
					{
						Line nested = _lines[_position];
						if (nested.Indent != indent + 2)
							throw new FormatException($"строка {nested.Number}: неверный отступ");
						AddMappingEntry(item, nested, indent + 2);
					}

					list.Add(KValue.Obj(item));
					continue;
				}

				list.Add(ParseScalar(rest));
			}

			return KValue.Obj(list);
		}

		private KValue ParseMapping(int indent)
		{
			var dict = new KDict(factory.Prim(PrimKind.Str), factory.Prim(PrimKind.Object));
			while (_position < _lines.Count)
			{
				Line line = _lines[_position];
				if (line.Indent < indent)
					break;
				if (line.Indent != indent)
					throw new FormatException($"строка {line.Number}: неверный отступ");
				if (line.Text.StartsWith("-", StringComparison.Ordinal))
					break;
				AddMappingEntry(dict, line, indent);
			}

			return KValue.Obj(dict);
		}

		private void AddMappingEntry(KDict dict, Line line, int indent)
		{
			int colon = line.Text.IndexOf(':');
			if (colon <= 0)
				throw new FormatException($"строка {line.Number}: ожидалось 'ключ: значение'");
			string key = line.Text[..colon].Trim();
			string rest = line.Text[(colon + 1)..].Trim();
			_position++;
			if (rest.Length > 0)
			{
				dict.Set(KValue.Str(key), ParseScalar(rest));
				return;
			}

			dict.Set(
				KValue.Str(key),
				_position < _lines.Count && _lines[_position].Indent > indent
					? ParseBlock(_lines[_position].Indent)
					: KValue.Nil);
		}

		private KValue ParseScalar(string text)
		{
			if (text.Length >= 2 && text[0] == '[' && text[^1] == ']')
			{
				var list = new KList(factory.Prim(PrimKind.Object));
				string inner = text[1..^1].Trim();
				if (inner.Length > 0)
					foreach (string part in SplitInline(inner))
						list.Add(ParseScalar(part));
				return KValue.Obj(list);
			}

			if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
				return KValue.Str(text[1..^1].Replace("''", "'"));
			if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
				return KValue.Str(text[1..^1]);

			return text switch
			{
				"null" or "~" => KValue.Nil,
				"true" => KValue.Bool(true),
				"false" => KValue.Bool(false),
				_ => Number(text) ?? KValue.Str(text),
			};
		}

		private static KValue? Number(string text)
		{
			if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
				return KValue.I64(integer);
			if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
				return KValue.F64(real);
			return null;
		}

		private static List<string> SplitInline(string text)
		{
			var parts = new List<string>();
			var current = new StringBuilder();
			var quoted = false;
			foreach (char symbol in text)
			{
				if (symbol == '"')
					quoted = !quoted;
				if (symbol == ',' && !quoted)
				{
					parts.Add(current.ToString().Trim());
					current.Clear();
					continue;
				}

				current.Append(symbol);
			}

			parts.Add(current.ToString().Trim());
			return parts;
		}

		private static List<Line> Build(string source)
		{
			var lines = new List<Line>();
			string[] raw = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
			for (var index = 0; index < raw.Length; index++)
			{
				string line = StripComment(raw[index]).TrimEnd();
				if (line.Trim().Length == 0)
					continue;
				int indent = line.Length - line.TrimStart().Length;
				lines.Add(new Line(index + 1, indent, line.Trim()));
			}

			return lines;
		}

		private static string StripComment(string line)
		{
			var quoted = false;
			for (var index = 0; index < line.Length; index++)
			{
				char symbol = line[index];
				if (symbol == '\'' || symbol == '"')
					quoted = !quoted;
				if (symbol == '#' && !quoted && (index == 0 || char.IsWhiteSpace(line[index - 1])))
					return line[..index];
			}

			return line;
		}

		private sealed record Line(int Number, int Indent, string Text);
	}
}
