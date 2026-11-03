using System.Globalization;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Formatting;
using Kern.Binding.Values;
using Kern.Vm.Values;

namespace Kern.Runtime.Text;

/// <summary>
/// Члены встроенного <c>str</c> (spec §7, §12). Первый параметр — <c>self</c>,
/// все методы — статические расширения; `len`/`is_empty` — настоящие свойства.
/// </summary>
[KernType("str", IsBuiltin = true)]
public static class StringApi
{
	[KernProperty]
	public static int Len(string self) => self.Length;

	[KernProperty]
	public static bool IsEmpty(string self) => self.Length == 0;

	[KernMember]
	public static string Upper(string self) => self.ToUpperInvariant();

	[KernMember]
	public static string Lower(string self) => self.ToLowerInvariant();

	[KernMember]
	public static string Trim(string self) => self.Trim();

	[KernMember]
	public static string TrimStart(string self) => self.TrimStart();

	[KernMember]
	public static string TrimEnd(string self) => self.TrimEnd();

	[KernMember]
	public static bool Contains(string self, string value) => self.Contains(value, StringComparison.Ordinal);

	[KernMember]
	public static bool StartsWith(string self, string value) => self.StartsWith(value, StringComparison.Ordinal);

	[KernMember]
	public static bool EndsWith(string self, string value) => self.EndsWith(value, StringComparison.Ordinal);

	[KernMember]
	public static int IndexOf(string self, string value) => self.IndexOf(value, StringComparison.Ordinal);

	[KernMember]
	public static int LastIndexOf(string self, string value) => self.LastIndexOf(value, StringComparison.Ordinal);

	[KernMember]
	public static string Substring(string self, int start)
	{
		int index = Normalize(start, self.Length);
		return self[index..];
	}

	[KernMember]
	public static string Substring(string self, int start, int length)
	{
		int index = Normalize(start, self.Length);
		if (length < 0 || index + length > self.Length)
			throw new Kern.Vm.Execution.KernPanic($"substring: длина {length} выходит за границы строки {self.Length}");
		return self.Substring(index, length);
	}

	[KernMember]
	public static string Replace(string self, string oldValue, string newValue) =>
		self.Replace(oldValue, newValue, StringComparison.Ordinal);

	[KernName("split")]
	[KernMember]
	public static KList<string> Split(string self, string separator)
	{
		string[] parts = self.Split(separator, StringSplitOptions.None);
		return MakeList(parts);
	}

	[KernName("chars")]
	[KernMember]
	public static KList<char> Chars(string self) => MakeCharList(self);

	/// <summary><c>s.graphemes()</c>: пользовательские графемные кластеры (Unicode).</summary>
	[KernMember]
	public static KList<string> Graphemes(string self)
	{
		var items = new List<string>();
		TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(self);
		while (enumerator.MoveNext())
			items.Add(enumerator.GetTextElement());
		return MakeList(items);
	}

	/// <summary><c>s.grapheme_len</c>: число графемных кластеров.</summary>
	[KernName("grapheme_len")]
	[KernProperty]
	public static int GraphemeLen(string self)
	{
		var count = 0;
		TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(self);
		while (enumerator.MoveNext())
			count++;
		return count;
	}

	/// <summary><c>s.grapheme_at(i)</c>: графема по индексу (отрицательный — с конца).</summary>
	[KernName("grapheme_at")]
	[KernMember]
	public static string GraphemeAt(string self, int index)
	{
		var items = new List<string>();
		TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(self);
		while (enumerator.MoveNext())
			items.Add(enumerator.GetTextElement());
		int normalized = index < 0 ? items.Count + index : index;
		if (normalized < 0 || normalized >= items.Count)
			throw new Kern.Vm.Execution.KernPanic($"grapheme_at: индекс {index} вне строки из {items.Count} графем");
		return items[normalized];
	}

	/// <summary><c>s.normalize(form)</c>: Unicode NFC/NFD/NFKC/NFKD.</summary>
	[KernMember]
	public static string Normalize(string self, string form) => form switch
	{
		"NFC" => self.Normalize(NormalizationForm.FormC),
		"NFD" => self.Normalize(NormalizationForm.FormD),
		"NFKC" => self.Normalize(NormalizationForm.FormKC),
		"NFKD" => self.Normalize(NormalizationForm.FormKD),
		_ => throw new Kern.Vm.Execution.KernPanic($"normalize: неизвестная форма '{form}' (NFC, NFD, NFKC, NFKD)"),
	};

	private static KList<string> MakeList(IEnumerable<string> items) =>
		new(KValue.Obj(new Kern.Vm.Values.KList(null, items.Select(KValue.Str))));

	private static KList<char> MakeCharList(IEnumerable<char> items) =>
		new(KValue.Obj(new Kern.Vm.Values.KList(null, items.Select(KValue.Char))));

	[KernMember]
	public static bool IsBlank(string self) => string.IsNullOrWhiteSpace(self);

	/// <summary>Целое по инвариантной культуре; nil при ошибке разбора или переполнении.</summary>
	[KernName("to_i32")]
	[KernMember]
	public static int? ToI32(string self) =>
		int.TryParse(self, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

	[KernName("to_i64")]
	[KernMember]
	public static long? ToI64(string self) =>
		long.TryParse(self, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;

	/// <summary>Дробное по инвариантной культуре (обычные формы с точкой и экспонентой); nil — не разобралось.</summary>
	[KernName("to_f64")]
	[KernMember]
	public static double? ToF64(string self) =>
		double.TryParse(self, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
			? value
			: null;

	/// <summary>Строго <c>"true"</c>/<c>"false"</c> (ordinal); иначе nil.</summary>
	[KernName("to_bool")]
	[KernMember]
	public static bool? ToBool(string self) => self switch
	{
		"true" => true,
		"false" => false,
		_ => null
	};

	[KernMember]
	public static string Repeat(string self, int count)
	{
		if (count < 0)
			throw new Kern.Vm.Execution.KernPanic($"repeat: отрицательное число повторов {count}");
		return string.Concat(Enumerable.Repeat(self, count));
	}

	[KernMember]
	public static string PadLeft(string self, int width, char fill) => self.PadLeft(width, fill);

	[KernMember]
	public static string PadRight(string self, int width, char fill) => self.PadRight(width, fill);

	[KernName("lines")]
	[KernMember]
	public static KList<string> Lines(string self) =>
		MakeList(self.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));

	[KernName("join")]
	[KernMember]
	public static string Join(KValue self, string separator) => CollectionFormat.Join(self, separator);

	private static int Normalize(int index, int length) =>
		index < 0 ? length + index : index;
}

/// <summary>Члены встроенного <c>char</c>.</summary>
[KernType("char", IsBuiltin = true)]
public static class CharApi
{
	[KernProperty]
	public static int Code(char self) => self;

	[KernMember]
	public static bool IsDigit(char self) => char.IsDigit(self);

	[KernMember]
	public static bool IsLetter(char self) => char.IsLetter(self);

	[KernMember]
	public static bool IsLetterOrDigit(char self) => char.IsLetterOrDigit(self);

	[KernMember]
	public static bool IsWhiteSpace(char self) => char.IsWhiteSpace(self);

	[KernMember]
	public static bool IsUpper(char self) => char.IsUpper(self);

	[KernMember]
	public static bool IsLower(char self) => char.IsLower(self);

	[KernMember]
	public static char ToUpper(char self) => char.ToUpperInvariant(self);

	[KernMember]
	public static char ToLower(char self) => char.ToLowerInvariant(self);

	[KernName("to_str")]
	[KernMember]
	public static string ToStr(char self) => self.ToString();
}

/// <summary>Форматирование коллекций без циклической зависимости от модуля списков.</summary>
internal static class CollectionFormat
{
	public static string Join(KValue value, string separator)
	{
		if (value.Ref is not Kern.Vm.Values.KList list)
			throw new Kern.Vm.Execution.KernPanic($"join: ожидался list, получено {value.TypeName}");
		return string.Join(separator, list.Items.Select(KernFormat.Value));
	}
}
