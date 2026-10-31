using Kern.Binding.Attributes;

namespace Kern.Runtime.Text;

/// <summary>Члены встроенного типа char.</summary>
[KernType("char")]
public static class CharApi
{
	[KernProperty] public static char Uppercase(char self) => char.ToUpperInvariant(self);
	[KernProperty] public static char Lowercase(char self) => char.ToLowerInvariant(self);
	[KernProperty] public static bool IsDigit(char self) => char.IsDigit(self);
	[KernProperty] public static bool IsAlpha(char self) => char.IsLetter(self);
	[KernProperty] public static bool IsAlnum(char self) => char.IsLetterOrDigit(self);
	[KernProperty] public static bool IsUpper(char self) => char.IsUpper(self);
	[KernProperty] public static bool IsLower(char self) => char.IsLower(self);
	[KernProperty] public static bool IsSpace(char self) => char.IsWhiteSpace(self);
	[KernProperty] public static bool IsPunct(char self) => char.IsPunctuation(self) || char.IsSymbol(self);
	[KernProperty] public static bool IsHexDigit(char self) => char.IsAsciiHexDigit(self);
	[KernProperty] public static int Code(char self) => self;

	/// <summary>Числовое значение цифры (0..9, a..f для шестнадцатеричных) либо -1.</summary>
	[KernProperty] public static int DigitValue(char self) =>
		self is >= '0' and <= '9' ? self - '0'
		: self is >= 'a' and <= 'f' ? self - 'a' + 10
		: self is >= 'A' and <= 'F' ? self - 'A' + 10 : -1;

	[KernMember] public static string ToStr(char self) => self.ToString();
	[KernMember] public static string Repeat(char self, int count) => new(self, System.Math.Max(0, count));
	[KernMember] public static bool EqualsIgnoreCase(char self, char other) => char.ToUpperInvariant(self) == char.ToUpperInvariant(other);

	/// <summary>Символ по коду: char.from_code(65) → 'A'.</summary>
	[KernStatic] public static char FromCode(int code) => (char)code;
}
