using System.Text.RegularExpressions;
using Kern.Binding.Attributes;

namespace Kern.Runtime.Ansi;

/// <summary>
/// <c>kern.ansi</c> (spec §16): ANSI-цвета и стили. Функции возвращают строку с
/// escape-последовательностями; <c>ansi.strip</c> убирает их. В перенаправленный вывод
/// цвета не включаются, если не установлен <c>NO_COLOR</c>? Наоборот: <c>supports</c>
/// сообщает, стоит ли включать цвета (не перенаправленный stdout и нет <c>NO_COLOR</c>).
/// </summary>
[KernModule("kern.ansi")]
public static class AnsiApi
{
	private static readonly System.Text.RegularExpressions.Regex Escape =
		new("\u001b\\[[0-9;]*m", System.Text.RegularExpressions.RegexOptions.Compiled);

	[KernMember]
	public static string Red(string text) => Wrap(31, text);

	[KernMember]
	public static string Green(string text) => Wrap(32, text);

	[KernMember]
	public static string Yellow(string text) => Wrap(33, text);

	[KernMember]
	public static string Blue(string text) => Wrap(34, text);

	[KernMember]
	public static string Magenta(string text) => Wrap(35, text);

	[KernMember]
	public static string Cyan(string text) => Wrap(36, text);

	[KernMember]
	public static string White(string text) => Wrap(37, text);

	[KernMember]
	public static string Gray(string text) => Wrap(90, text);

	[KernMember]
	public static string Bold(string text) => Wrap(1, text);

	[KernMember]
	public static string Dim(string text) => Wrap(2, text);

	[KernMember]
	public static string Italic(string text) => Wrap(3, text);

	[KernMember]
	public static string Underline(string text) => Wrap(4, text);

	/// <summary><c>reset(text)</c>: явно добавить сброс после текста.</summary>
	[KernMember]
	public static string Reset(string text) => text + "\u001b[0m";

	/// <summary><c>strip(text)</c>: убрать ANSI-последовательности.</summary>
	[KernMember]
	public static string Strip(string text) => Escape.Replace(text, "");

	/// <summary><c>color256(n, text)</c>: 256-цветный передний план.</summary>
	[KernMember]
	public static string Color256(int color, string text) =>
		$"\u001b[38;5;{System.Math.Clamp(color, 0, 255)}m{text}\u001b[0m";

	/// <summary><c>rgb(r, g, b, text)</c>: truecolor передний план.</summary>
	[KernMember]
	public static string Rgb(int red, int green, int blue, string text) =>
		$"\u001b[38;2;{Clamp(red)};{Clamp(green)};{Clamp(blue)}m{text}\u001b[0m";

	/// <summary><c>ansi.supports</c>: включать ли цвета в текущем процессе.</summary>
	[KernProperty]
	public static bool Supports() =>
		!Console.IsOutputRedirected &&
		string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

	private static string Wrap(int code, string text) =>
		Supports() ? $"\u001b[{code}m{text}\u001b[0m" : text;

	private static int Clamp(int value) => System.Math.Clamp(value, 0, 255);
}
