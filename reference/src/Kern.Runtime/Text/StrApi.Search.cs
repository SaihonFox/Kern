using Kern.Binding.Attributes;

namespace Kern.Runtime.Text;

// Поиск и проверки. Индексы — по UTF-16 кодовым единицам, как в .NET; -1 = не найдено.
public static partial class StrApi
{
	[KernMember] public static bool Contains(string self, string part) => self.Contains(part, StringComparison.Ordinal);
	[KernMember] public static bool Contains(string self, char c) => self.Contains(c);
	[KernMember] public static bool ContainsIgnoreCase(string self, string part) => self.Contains(part, StringComparison.OrdinalIgnoreCase);
	[KernMember] public static bool StartsWith(string self, string prefix) => self.StartsWith(prefix, StringComparison.Ordinal);
	[KernMember] public static bool StartsWith(string self, char c) => self.Length > 0 && self[0] == c;
	[KernMember] public static bool EndsWith(string self, string suffix) => self.EndsWith(suffix, StringComparison.Ordinal);
	[KernMember] public static bool EndsWith(string self, char c) => self.Length > 0 && self[^1] == c;

	[KernMember] public static int IndexOf(string self, string part, int start = 0) => self.IndexOf(part, start, StringComparison.Ordinal);
	[KernMember] public static int IndexOf(string self, char c, int start = 0) => self.IndexOf(c, start);
	[KernMember] public static int LastIndexOf(string self, string part) => self.LastIndexOf(part, StringComparison.Ordinal);
	[KernMember] public static int LastIndexOf(string self, char c) => self.LastIndexOf(c);

	[KernMember] public static int Count(string self, string part)
	{
		if (part.Length == 0) return 0;
		int n = 0, i = 0;
		while ((i = self.IndexOf(part, i, StringComparison.Ordinal)) >= 0) { n++; i += part.Length; }
		return n;
	}

	[KernMember] public static string RemovePrefix(string self, string prefix) =>
		prefix.Length > 0 && self.StartsWith(prefix, StringComparison.Ordinal) ? self[prefix.Length..] : self;
	[KernMember] public static string RemoveSuffix(string self, string suffix) =>
		suffix.Length > 0 && self.EndsWith(suffix, StringComparison.Ordinal) ? self[..^suffix.Length] : self;

	/// <summary>Часть строки до первого вхождения разделителя (вся строка, если не найден).</summary>
	[KernMember] public static string Before(string self, string sep) { int i = self.IndexOf(sep, StringComparison.Ordinal); return i < 0 ? self : self[..i]; }
	/// <summary>Часть строки после первого вхождения разделителя (пусто, если не найден).</summary>
	[KernMember] public static string After(string self, string sep) { int i = self.IndexOf(sep, StringComparison.Ordinal); return i < 0 ? "" : self[(i + sep.Length)..]; }

	[KernProperty] public static bool IsNumeric(string self) => self.Length > 0 && double.TryParse(self, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
	[KernProperty] public static bool IsAlpha(string self) => self.Length > 0 && self.All(char.IsLetter);
	[KernProperty] public static bool IsDigits(string self) => self.Length > 0 && self.All(char.IsDigit);
	[KernProperty] public static bool IsAlnum(string self) => self.Length > 0 && self.All(char.IsLetterOrDigit);
}
