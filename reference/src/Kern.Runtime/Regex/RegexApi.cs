using System.Text.RegularExpressions;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Runtime.Regex;

/// <summary>
/// Регулярные выражения (.NET-синтаксис): <c>regex r("\\d+")</c>, <c>r.is_match(s)</c>, <c>r.find(s)</c>, <c>r.replace(s, "x")</c>.
/// Неверный шаблон — panic при создании; для проверки без panic есть <c>regex.try_new</c>.
/// </summary>
[KernType("regex")]
public sealed class KernRegex
{
	private readonly System.Text.RegularExpressions.Regex _re;

	[KernCtor] public KernRegex(string pattern) : this(pattern, "") { }

	/// <summary>flags: i — без регистра, m — многострочный, s — точка = любой символ, x — пробелы в шаблоне игнорируются.</summary>
	[KernCtor] public KernRegex(string pattern, string flags)
	{
		var o = RegexOptions.CultureInvariant;
		foreach (char c in flags)
			o |= c switch
			{
				'i' => RegexOptions.IgnoreCase,
				'm' => RegexOptions.Multiline,
				's' => RegexOptions.Singleline,
				'x' => RegexOptions.IgnorePatternWhitespace,
				_ => throw new KernPanic($"regex: неизвестный флаг '{c}' (допустимы i m s x)"),
			};
		try { _re = new System.Text.RegularExpressions.Regex(pattern, o); }
		catch (ArgumentException e) { throw new KernPanic("regex: неверный шаблон: " + e.Message); }
		Pattern = pattern;
	}

	[KernProperty] public string Pattern { get; }

	[KernStatic] public static KResult<KernRegex, string> TryNew(string pattern, string flags = "")
	{
		try { return KResult<KernRegex, string>.Ok(KValue.Obj(new KernRegex(pattern, flags))); }
		catch (KernPanic e) { return Kv.Err<KernRegex>(e.Message); }
	}

	[KernStatic] public static string Escape(string text) => System.Text.RegularExpressions.Regex.Escape(text);

	[KernMember] public bool IsMatch(string text) => _re.IsMatch(text);

	/// <summary>Первое совпадение или nil.</summary>
	[KernMember] public KernMatch? Find(string text) => _re.Match(text) is { Success: true } m ? new KernMatch(m) : null;

	[KernMember] public KList<KernMatch> FindAll(string text) =>
		Kv.List<KernMatch>(_re.Matches(text).Select(m => KValue.Obj(new KernMatch(m))));

	[KernMember] public string Replace(string text, string replacement) => _re.Replace(text, replacement);

	[KernMember] public string Replace(string text, KFunc<KernMatch, string> f) =>
		_re.Replace(text, m => f.Invoke(KValue.Obj(new KernMatch(m))).AsStr);

	[KernMember] public KList<string> Split(string text) => Kv.Strs(_re.Split(text));

	[KernMember] public override string ToString() => Pattern;
}

/// <summary>Результат поиска: текст, позиция и группы (по номеру или имени).</summary>
[KernType("match")]
public sealed class KernMatch(Match match)
{
	[KernProperty] public string Text => match.Value;
	[KernProperty] public int Start => match.Index;
	[KernProperty] public int Len => match.Length;
	[KernProperty] public int End => match.Index + match.Length;

	/// <summary>Группы с номера 1 (группа 0 — всё совпадение); не участвовавшая группа даёт пустую строку.</summary>
	[KernProperty] public KList<string> Groups => Kv.Strs(match.Groups.Cast<Group>().Skip(1).Select(g => g.Value));

	[KernMember] public string? Group(string name) => match.Groups[name] is { Success: true } g ? g.Value : null;
	[KernMember] public string Group(int index) =>
		index >= 0 && index < match.Groups.Count ? match.Groups[index].Value : throw new KernPanic($"match.group: нет группы {index}");
}
