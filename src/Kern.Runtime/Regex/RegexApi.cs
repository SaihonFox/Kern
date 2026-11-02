using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Vm.Execution;
using Kern.Vm.Values;

using NetRegex = System.Text.RegularExpressions.Regex;
using NetMatch = System.Text.RegularExpressions.Match;
using NetRegexOptions = System.Text.RegularExpressions.RegexOptions;

namespace Kern.Runtime.Regex;

/// <summary>
/// Скомпилированное регулярное выражение — значение Kern-типа <c>regex</c> (spec §12, §18c).
/// Создаётся фабриками <c>regex(шаблон[, флаги])</c> и <c>try_new(шаблон[, флаги])</c> модуля
/// <c>kern.regex</c>. В VM значение живёт в <see cref="KNativeBox"/>, методы и свойства —
/// в <see cref="RegexMembers"/>. Флаги: <c>i</c> — без регистра, <c>m</c> — многострочный,
/// <c>s</c> — точка включает перевод строки, <c>x</c> — пробелы в шаблоне игнорируются.
/// </summary>
[KernType("regex")]
public sealed class KernRegex
{
	private KernRegex(string pattern, string flags, NetRegexOptions options, NetRegex compiled)
	{
		Pattern = pattern;
		Flags = flags;
		Options = options;
		Compiled = compiled;
	}

	/// <summary>Исходный шаблон без изменений.</summary>
	public string Pattern { get; }

	/// <summary>Канонический набор флагов: буквы в порядке <c>imsx</c>.</summary>
	public string Flags { get; }

	/// <summary>Разобранные опции .NET.</summary>
	public NetRegexOptions Options { get; }

	internal NetRegex Compiled { get; }

	/// <summary>
	/// Создать значение; неверный шаблон или неизвестный флаг — <see cref="KernPanic"/>
	/// с русским сообщением (текст исключения .NET добавляется для диагностики).
	/// </summary>
	internal static KernRegex Create(string pattern, string flags)
	{
		if (pattern is null)
			throw new KernPanic("regex: шаблон не может быть nil");

		NetRegexOptions options = ParseFlags(flags);
		try
		{
			return new KernRegex(pattern, CanonicalFlags(options), options, new NetRegex(pattern, options));
		}
		catch (ArgumentException exception)
		{
			throw new KernPanic($"regex: неверный шаблон '{pattern}': {exception.Message}", exception);
		}
	}

	/// <summary>Вариант без паники: <c>nil</c> вместо <see cref="KernPanic"/>.</summary>
	internal static KernRegex? TryCreate(string pattern, string flags)
	{
		try
		{
			return Create(pattern, flags);
		}
		catch (KernPanic)
		{
			return null;
		}
	}

	/// <summary>Печать значения в Kern: сам шаблон.</summary>
	public override string ToString() => Pattern;

	private static NetRegexOptions ParseFlags(string? flags)
	{
		NetRegexOptions options = NetRegexOptions.CultureInvariant;
		foreach (char flag in flags ?? "")
		{
			switch (flag)
			{
				case 'i':
					options |= NetRegexOptions.IgnoreCase;
					break;
				case 'm':
					options |= NetRegexOptions.Multiline;
					break;
				case 's':
					options |= NetRegexOptions.Singleline;
					break;
				case 'x':
					options |= NetRegexOptions.IgnorePatternWhitespace;
					break;
				case ' ' or ',' or '\t':
					break;
				default:
					throw new KernPanic($"regex: неизвестный флаг '{flag}' (допустимы i m s x)");
			}
		}

		return options;
	}

	private static string CanonicalFlags(NetRegexOptions options)
	{
		var text = new StringBuilder(4);
		if ((options & NetRegexOptions.IgnoreCase) != 0)
			text.Append('i');
		if ((options & NetRegexOptions.Multiline) != 0)
			text.Append('m');
		if ((options & NetRegexOptions.Singleline) != 0)
			text.Append('s');
		if ((options & NetRegexOptions.IgnorePatternWhitespace) != 0)
			text.Append('x');
		return text.ToString();
	}
}

/// <summary>
/// Члены Kern-типа <c>regex</c> (spec §18c): <c>is_match/find/find_all/replace/split</c>,
/// свойства <c>pattern</c>/<c>options</c>. Первый параметр — <c>self</c> из <see cref="KNativeBox"/>.
/// </summary>
[KernType("regex")]
public static class RegexMembers
{
	/// <summary><c>pattern</c>: исходный шаблон.</summary>
	[KernProperty]
	public static string Pattern(KNativeBox self) => Require(self).Pattern;

	/// <summary><c>options</c>: флаги в каноническом виде (<c>imsx</c>).</summary>
	[KernProperty]
	public static string Options(KNativeBox self) => Require(self).Flags;

	/// <summary><c>is_match(text)</c>: есть ли хотя бы одно совпадение.</summary>
	[KernMember]
	public static bool IsMatch(KNativeBox self, string text) => Require(self).Compiled.IsMatch(text);

	/// <summary><c>find(text)</c>: первое совпадение или <c>nil</c>.</summary>
	[KernMember]
	public static string? Find(KNativeBox self, string text)
	{
		NetMatch match = Require(self).Compiled.Match(text);
		return match.Success ? match.Value : null;
	}

	/// <summary><c>find_all(text)</c>: все совпадения по порядку (пустой список — нет совпадений).</summary>
	[KernName("find_all")]
	[KernMember]
	public static KList<string> FindAll(KNativeBox self, string text) =>
		Strings(Require(self).Compiled.Matches(text).Select(match => match.Value));

	/// <summary><c>replace(text, replacement)</c>: заменить все совпадения (шаблон замены .NET: <c>$1</c>).</summary>
	[KernMember]
	public static string Replace(KNativeBox self, string text, string replacement) =>
		Require(self).Compiled.Replace(text, replacement);

	/// <summary><c>split(text)</c>: части между совпадениями.</summary>
	[KernMember]
	public static KList<string> Split(KNativeBox self, string text) =>
		Strings(Require(self).Compiled.Split(text));

	/// <summary>
	/// <c>find_match(text)</c>: совпадение с позициями и группами или <c>nil</c> (spec §18c).
	/// Имя <c>match</c> из спеки не парсится после точки — это ключевое слово (Q32).
	/// </summary>
	[KernName("find_match")]
	[KernMember]
	public static KernRegexMatch? Match(KNativeBox self, string text)
	{
		NetMatch match = Require(self).Compiled.Match(text);
		return match.Success ? KernRegexMatch.From(match) : null;
	}

	/// <summary><c>match(text)</c>: то же, что <c>find_match</c> (Q32, имя типа <c>regex_match</c>).</summary>
	[KernName("match")]
	[KernMember]
	public static KernRegexMatch? MatchResult(KNativeBox self, string text)
	{
		NetMatch match = Require(self).Compiled.Match(text);
		return match.Success ? KernRegexMatch.From(match) : null;
	}

	internal static KernRegex Require(KNativeBox self) => self.Value switch
	{
		KernRegex regex => regex,
		_ => throw new KernPanic($"ожидался regex, получено {self.Value.GetType().Name}"),
	};

	internal static KList<string> Strings(IEnumerable<string> items) =>
		new(KValue.Obj(new Kern.Vm.Values.KList(null, items.Select(KValue.Str))));
}

/// <summary>
/// Модуль <c>kern.regex</c> (spec §18c): фабрики <c>regex(шаблон[, флаги])</c> и
/// <c>try_new(шаблон[, флаги])</c>. Неверный шаблон в <c>regex</c> — <see cref="KernPanic"/>
/// по-русски; <c>try_new</c> возвращает <c>nil</c>.
/// </summary>
[KernModule("kern.regex")]
public static class RegexModule
{
	/// <summary><c>regex(pattern)</c>: скомпилировать шаблон без флагов.</summary>
	[KernName("regex")]
	[KernMember]
	public static KernRegex New(string pattern) => KernRegex.Create(pattern, "");

	/// <summary><c>regex(pattern, flags)</c>: скомпилировать шаблон с флагами <c>imsx</c>.</summary>
	[KernName("regex")]
	[KernMember]
	public static KernRegex New(string pattern, string flags) => KernRegex.Create(pattern, flags);

	/// <summary><c>try_new(pattern)</c>: <c>nil</c> вместо паники при неверном шаблоне.</summary>
	[KernName("try_new")]
	[KernMember]
	public static KernRegex? TryNew(string pattern) => KernRegex.TryCreate(pattern, "");

	/// <summary><c>try_new(pattern, flags)</c>: <c>nil</c> при неверном шаблоне или флаге.</summary>
	[KernName("try_new")]
	[KernMember]
	public static KernRegex? TryNew(string pattern, string flags) => KernRegex.TryCreate(pattern, flags);
}

/// <summary>
/// Статические члены типа <c>regex</c>: позволяют писать <c>regex.try_new(…)</c> наряду
/// с модульной формой <c>try_new(…)</c> (spec §18c).
/// </summary>
[KernType("regex")]
public static class RegexStaticApi
{
	[KernStatic]
	[KernName("try_new")]
	public static KernRegex? TryNew(string pattern) => KernRegex.TryCreate(pattern, "");

	[KernStatic]
	[KernName("try_new")]
	public static KernRegex? TryNew(string pattern, string flags) => KernRegex.TryCreate(pattern, flags);
}
