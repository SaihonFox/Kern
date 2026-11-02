using System.Globalization;
using Kern.Binding.Attributes;
using Kern.Vm.Values;

namespace Kern.Runtime.Text;

/// <summary>
/// <c>kern.text</c> (M6, spec §12): функциональные формы работы со строками для кода,
/// которому удобнее модуль, чем члены <c>str</c>. Все функции чистые, без паник:
/// неверный разбор возвращает <c>nil</c>.
/// </summary>
[KernModule("kern.text")]
public static partial class TextModule
{
	/// <summary><c>text.len(s)</c>: длина строки в UTF-16 единицах.</summary>
	[KernMember]
	public static int Len(string text) => text.Length;

	/// <summary><c>text.is_empty(s)</c>: пустая строка.</summary>
	[KernName("is_empty")]
	[KernMember]
	public static bool IsEmpty(string text) => text.Length == 0;

	/// <summary><c>text.upper(s)</c>: верхний регистр (инвариантная культура).</summary>
	[KernMember]
	public static string Upper(string text) => text.ToUpperInvariant();

	/// <summary><c>text.lower(s)</c>: нижний регистр.</summary>
	[KernMember]
	public static string Lower(string text) => text.ToLowerInvariant();

	/// <summary><c>text.trim(s)</c>: обрезать пробелы с обеих сторон.</summary>
	[KernMember]
	public static string Trim(string text) => text.Trim();

	/// <summary><c>text.trim_start(s)</c>: обрезать пробелы слева.</summary>
	[KernName("trim_start")]
	[KernMember]
	public static string TrimStart(string text) => text.TrimStart();

	/// <summary><c>text.trim_end(s)</c>: обрезать пробелы справа.</summary>
	[KernName("trim_end")]
	[KernMember]
	public static string TrimEnd(string text) => text.TrimEnd();

	/// <summary><c>text.contains(s, part)</c>: есть ли подстрока.</summary>
	[KernMember]
	public static bool Contains(string text, string part) =>
		text.Contains(part, StringComparison.Ordinal);

	/// <summary><c>text.starts_with(s, prefix)</c>: начинается с префикса.</summary>
	[KernName("starts_with")]
	[KernMember]
	public static bool StartsWith(string text, string prefix) =>
		text.StartsWith(prefix, StringComparison.Ordinal);

	/// <summary><c>text.ends_with(s, suffix)</c>: заканчивается суффиксом.</summary>
	[KernName("ends_with")]
	[KernMember]
	public static bool EndsWith(string text, string suffix) =>
		text.EndsWith(suffix, StringComparison.Ordinal);

	/// <summary><c>text.replace(s, old, new)</c>: заменить все вхождения.</summary>
	[KernMember]
	public static string Replace(string text, string oldValue, string newValue) =>
		text.Replace(oldValue, newValue, StringComparison.Ordinal);

	/// <summary><c>text.repeat(s, count)</c>: повторить строку.</summary>
	[KernMember]
	public static string Repeat(string text, int count)
	{
		if (count < 0)
			throw new Kern.Vm.Execution.KernPanic("text.repeat: отрицательное число повторов");
		return string.Concat(Enumerable.Repeat(text, count));
	}

	/// <summary><c>text.split(s, sep)</c>: части строки.</summary>
	[KernMember]
	public static Kern.Binding.Values.KList<string> Split(string text, string separator) =>
		Strings(text.Split(separator, StringSplitOptions.None));

	/// <summary><c>text.lines(s)</c>: строки текста (без завершающего перевода).</summary>
	[KernMember]
	public static Kern.Binding.Values.KList<string> Lines(string text) =>
		Strings(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));

	/// <summary><c>text.join(parts, sep)</c>: склеить части.</summary>
	[KernMember]
	public static string Join(Kern.Binding.Values.KList<string> parts, string separator) =>
		string.Join(separator, StringsOf(parts));

	/// <summary><c>text.chars(s)</c>: символы строки.</summary>
	[KernMember]
	public static Kern.Binding.Values.KList<char> Chars(string text) =>
		new Kern.Binding.Values.KList<char>(KValue.Obj(new Kern.Vm.Values.KList(null, text.Select(KValue.Char))));

	/// <summary><c>text.from_chars(chars)</c>: строка из символов.</summary>
	[KernName("from_chars")]
	[KernMember]
	public static string FromChars(Kern.Binding.Values.KList<char> chars) =>
		new string([.. CharsOf(chars)]);

	/// <summary><c>text.parse_i32(s)</c>: целое или <c>nil</c> (инвариантная культура).</summary>
	[KernName("parse_i32")]
	[KernMember]
	public static int? ParseI32(string text) =>
		int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

	/// <summary><c>text.parse_i64(s)</c>: длинное целое или <c>nil</c>.</summary>
	[KernName("parse_i64")]
	[KernMember]
	public static long? ParseI64(string text) =>
		long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;

	/// <summary><c>text.parse_f64(s)</c>: дробное или <c>nil</c>.</summary>
	[KernName("parse_f64")]
	[KernMember]
	public static double? ParseF64(string text) =>
		double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

	/// <summary><c>text.pad_left(s, width, fill)</c>: дополнить слева.</summary>
	[KernName("pad_left")]
	[KernMember]
	public static string PadLeft(string text, int width, char fill) => text.PadLeft(width, fill);

	/// <summary><c>text.pad_right(s, width, fill)</c>: дополнить справа.</summary>
	[KernName("pad_right")]
	[KernMember]
	public static string PadRight(string text, int width, char fill) => text.PadRight(width, fill);

	private static Kern.Binding.Values.KList<string> Strings(IEnumerable<string> items) =>
		new Kern.Binding.Values.KList<string>(KValue.Obj(new Kern.Vm.Values.KList(null, items.Select(KValue.Str))));

	private static IEnumerable<string> StringsOf(KValue value)
	{
		if (value.Kind != ValueKind.Ref || value.Ref is not Kern.Vm.Values.KList list)
			throw new Kern.Vm.Execution.KernPanic($"text.join: ожидался list<str>, получено {value.TypeName}");
		foreach (KValue item in list.Items)
		{
			if (item.Kind != ValueKind.Str)
				throw new Kern.Vm.Execution.KernPanic($"text.join: ожидался str, получено {item.TypeName}");
			yield return item.AsStr;
		}
	}

	private static IEnumerable<char> CharsOf(KValue value)
	{
		if (value.Kind != ValueKind.Ref || value.Ref is not Kern.Vm.Values.KList list)
			throw new Kern.Vm.Execution.KernPanic($"text.from_chars: ожидался list<char>, получено {value.TypeName}");
		foreach (KValue item in list.Items)
		{
			if (item.Kind != ValueKind.Char)
				throw new Kern.Vm.Execution.KernPanic($"text.from_chars: ожидался char, получено {item.TypeName}");
			yield return item.AsChar;
		}
	}
}
