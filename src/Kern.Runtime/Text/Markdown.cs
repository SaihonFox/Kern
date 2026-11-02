using System.Text;

namespace Kern.Runtime.Text;

/// <summary>Транслитерация кириллицы в латиницу для slug и поиска (без внешних данных).</summary>
internal static class Transliteration
{
	private static readonly Dictionary<char, string> Letters = new()
	{
		['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e",
		['ё'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k",
		['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r",
		['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts",
		['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "",
		['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
		['і'] = "i", ['ї'] = "yi", ['є'] = "ye", ['ґ'] = "g",
		['ў'] = "u", ['ђ'] = "dj", ['ј'] = "j", ['љ'] = "lj", ['њ'] = "nj", ['ћ'] = "c",
		['џ'] = "dz", ['ш'] = "sh",
	};

	internal static string Map(char symbol)
	{
		char lower = char.ToLowerInvariant(symbol);
		if (!Letters.TryGetValue(lower, out string? mapped))
			return symbol.ToString();
		return char.IsUpper(symbol) ? Capitalize(mapped) : mapped;
	}

	private static string Capitalize(string value) =>
		value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}

/// <summary>Небольшой Markdown → HTML для документации и писем (без полного CommonMark).</summary>
internal static class Markdown
{
	public static string ToHtml(string text)
	{
		string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		var builder = new StringBuilder();
		bool code = false;
		bool list = false;
		foreach (string raw in lines)
		{
			string line = raw;
			if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
			{
				if (!code)
				{
					CloseList(builder, ref list);
					builder.Append("<pre><code>");
				}
				else
				{
					builder.Append("</code></pre>\n");
				}
				code = !code;
				continue;
			}
			if (code)
			{
				builder.Append(TextModule.EscapeHtml(line)).Append('\n');
				continue;
			}
			if (string.IsNullOrWhiteSpace(line))
			{
				CloseList(builder, ref list);
				continue;
			}
			if (line.Trim() is "---" or "***" or "___")
			{
				CloseList(builder, ref list);
				builder.Append("<hr>\n");
				continue;
			}
			if (TryHeading(line, out int level, out string title))
			{
				CloseList(builder, ref list);
				builder.Append($"<h{level}>{Inline(title)}</h{level}>\n");
				continue;
			}
			if (line.StartsWith("> ", StringComparison.Ordinal))
			{
				CloseList(builder, ref list);
				builder.Append($"<blockquote>{Inline(line[2..])}</blockquote>\n");
				continue;
			}
			if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
			{
				if (!list)
				{
					builder.Append("<ul>\n");
					list = true;
				}
				builder.Append($"<li>{Inline(line[2..])}</li>\n");
				continue;
			}
			CloseList(builder, ref list);
			builder.Append($"<p>{Inline(line)}</p>\n");
		}
		if (code) builder.Append("</code></pre>\n");
		CloseList(builder, ref list);
		return builder.ToString();
	}

	private static void CloseList(StringBuilder builder, ref bool list)
	{
		if (!list) return;
		builder.Append("</ul>\n");
		list = false;
	}

	private static bool TryHeading(string line, out int level, out string text)
	{
		level = 0;
		while (level < line.Length && line[level] == '#') level++;
		if (level is < 1 or > 6 || level >= line.Length || line[level] != ' ')
		{
			text = "";
			return false;
		}
		text = line[(level + 1)..];
		return true;
	}

	private static string Inline(string text)
	{
		string html = TextModule.EscapeHtml(text);
		html = System.Text.RegularExpressions.Regex.Replace(html, "`([^`]+)`", "<code>$1</code>");
		html = System.Text.RegularExpressions.Regex.Replace(html, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
		html = System.Text.RegularExpressions.Regex.Replace(html, @"\*(.+?)\*", "<em>$1</em>");
		html = System.Text.RegularExpressions.Regex.Replace(html, @"\[(.+?)\]\((.+?)\)", "<a href=\"$2\">$1</a>");
		return html;
	}
}
