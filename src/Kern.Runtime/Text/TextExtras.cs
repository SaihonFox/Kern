using System.Globalization;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Text;

/// <summary>
/// Расширения <c>kern.text</c>: расстояние Левенштейна, нечёткий поиск, транслитерация
/// и slug, нормализация Unicode и сегментация по графемам, простой Markdown → HTML,
/// подстановка <c>{{key}}</c> в шаблоне и экранирование HTML. Циклы/условия и частичные
/// шаблоны — следующий срез (см. docs/LIBRARIES.md).
/// </summary>
public static partial class TextModule
{
	/// <summary><c>text.levenshtein(a, b)</c>: расстояние редактирования (UTF-16 единицы).</summary>
	[KernMember]
	public static int Levenshtein(string a, string b)
	{
		if (a.Length == 0) return b.Length;
		if (b.Length == 0) return a.Length;
		int[] previous = new int[b.Length + 1];
		int[] current = new int[b.Length + 1];
		for (int j = 0; j <= b.Length; j++) previous[j] = j;
		for (int i = 1; i <= a.Length; i++)
		{
			current[0] = i;
			for (int j = 1; j <= b.Length; j++)
			{
				int cost = a[i - 1] == b[j - 1] ? 0 : 1;
				current[j] = System.Math.Min(System.Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
			}
			(previous, current) = (current, previous);
		}
		return previous[b.Length];
	}

	/// <summary><c>text.fuzzy_contains(text, part, max_distance)</c>: нечёткое вхождение.</summary>
	[KernName("fuzzy_contains")]
	[KernMember]
	public static bool FuzzyContains(string text, string part, int maxDistance)
	{
		if (maxDistance < 0)
			throw new KernPanic("text.fuzzy_contains: расстояние не может быть отрицательным");
		if (part.Length == 0) return true;
		if (part.Length > text.Length) return Levenshtein(text, part) <= maxDistance;
		for (int start = 0; start + part.Length <= text.Length; start++)
			if (Levenshtein(text.Substring(start, part.Length), part) <= maxDistance)
				return true;
		return false;
	}

	/// <summary><c>text.transliterate(text)</c>: кириллица → латиница (остальное без изменений).</summary>
	[KernMember]
	public static string Transliterate(string text)
	{
		var builder = new StringBuilder(text.Length * 2);
		foreach (char symbol in text)
			builder.Append(Kern.Runtime.Text.Transliteration.Map(symbol));
		return builder.ToString();
	}

	/// <summary><c>text.slug(text)</c>: URL-безопасный slug в нижнем регистре.</summary>
	[KernMember]
	public static string Slug(string text)
	{
		var builder = new StringBuilder(text.Length);
		bool dash = false;
		foreach (char symbol in Transliterate(text).ToLowerInvariant())
		{
			if (char.IsLetterOrDigit(symbol))
			{
				if (dash && builder.Length > 0) builder.Append('-');
				dash = false;
				builder.Append(symbol);
			}
			else
			{
				dash = true;
			}
		}
		return builder.ToString();
	}

	/// <summary><c>text.normalize(text, form)</c>: Unicode NFC/NFD/NFKC/NFKD.</summary>
	[KernMember]
	public static string Normalize(string text, string form) => form switch
	{
		"NFC" => text.Normalize(NormalizationForm.FormC),
		"NFD" => text.Normalize(NormalizationForm.FormD),
		"NFKC" => text.Normalize(NormalizationForm.FormKC),
		"NFKD" => text.Normalize(NormalizationForm.FormKD),
		_ => throw new KernPanic($"text.normalize: неизвестная форма '{form}' (NFC, NFD, NFKC, NFKD)"),
	};

	/// <summary><c>text.graphemes(text)</c>: кластеры графем (пользовательские символы).</summary>
	[KernMember]
	public static Kern.Binding.Values.KList<string> Graphemes(string text)
	{
		var items = new List<KValue>();
		TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
		while (enumerator.MoveNext())
			items.Add(KValue.Str(enumerator.GetTextElement()));
		return new Kern.Binding.Values.KList<string>(KValue.Obj(new Kern.Vm.Values.KList(null, items)));
	}

	/// <summary><c>text.escape_html(text)</c>: экранировать <c>&amp; &lt; &gt; " '</c>.</summary>
	[KernName("escape_html")]
	[KernMember]
	public static string EscapeHtml(string text) =>
		text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
			.Replace("\"", "&quot;").Replace("'", "&#39;");

	/// <summary>
	/// <c>text.template_render(template, values)</c>: заменить <c>{{key}}</c> значениями
	/// словаря (значения экранируются как HTML). Отсутствующий ключ остаётся как есть.
	/// </summary>
	[KernName("template_render")]
	[KernMember]
	public static string TemplateRender(string template, KValue values)
	{
		KDict? dict = values.Ref as KDict;
		if (dict is null)
			throw new KernPanic($"text.template_render: ожидался dict<str, str>, получено {values.TypeName}");
		var builder = new StringBuilder(template.Length);
		int position = 0;
		while (position < template.Length)
		{
			int open = template.IndexOf("{{", position, StringComparison.Ordinal);
			if (open < 0)
			{
				builder.Append(template, position, template.Length - position);
				break;
			}
			builder.Append(template, position, open - position);
			int close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
			if (close < 0)
			{
				builder.Append(template, open, template.Length - open);
				break;
			}
			string key = template[(open + 2)..close].Trim();
			if (dict.TryGet(KValue.Str(key), out KValue value) && value.Kind == ValueKind.Str)
				builder.Append(EscapeHtml(value.AsStr));
			else
				builder.Append(template, open, close - open + 2);
			position = close + 2;
		}
		return builder.ToString();
	}

	/// <summary><c>text.to_utf8(text)</c>: UTF-8 байты строки.</summary>
	[KernName("to_utf8")]
	[KernMember]
	[KernReturn("list<u8>")]
	public static KValue ToUtf8(Interpreter interpreter, string text)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(text);
		return KValue.Obj(new Kern.Vm.Values.KList(
			interpreter.Types.Prim(Kern.Bytecode.PrimKind.U8), bytes.Select(KValue.U8)));
	}

	/// <summary><c>text.from_utf8(bytes)</c>: строка из UTF-8 или <c>Err</c>.</summary>
	[KernName("from_utf8")]
	[KernMember]
	[KernReturn("result<str, str>")]
	public static Kern.Binding.Values.KResult<string, string> FromUtf8(KValue bytes)
	{
		if (bytes.Ref is not Kern.Vm.Values.KList list)
			return Kern.Binding.Values.KResult<string, string>.Failure(
				$"text.from_utf8: ожидался list<u8>, получено {bytes.TypeName}");
		var data = new byte[list.Count];
		for (int index = 0; index < data.Length; index++)
			data[index] = list[index].AsU8;
		try
		{
			return Kern.Binding.Values.KResult<string, string>.Success(
				new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data));
		}
		catch (DecoderFallbackException)
		{
			return Kern.Binding.Values.KResult<string, string>.Failure(
				"text.from_utf8: неверная UTF-8 последовательность");
		}
	}

	/// <summary>
	/// <c>text.markdown_to_html(text)</c>: заголовки, списки, цитаты, блоки кода,
	/// <c>**жирный**</c>, <c>*курсив*</c>, <c>`код`</c> и ссылки <c>[текст](url)</c>.
	/// </summary>
	[KernName("markdown_to_html")]
	[KernMember]
	public static string MarkdownToHtml(string text) => Markdown.ToHtml(text);
}
