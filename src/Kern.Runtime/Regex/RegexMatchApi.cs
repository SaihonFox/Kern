using System.Text.RegularExpressions;
using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Vm.Execution;
using Kern.Vm.Values;

using NetMatch = System.Text.RegularExpressions.Match;

namespace Kern.Runtime.Regex;

/// <summary>
/// Результат <c>regex.match(text)</c> (spec §18c, Q32: имя <c>regex_match</c>, потому что
/// <c>match</c> — ключевое слово). Хранит текст, позиции, полный набор групп и именованные
/// группы; члены доступны через <see cref="RegexMatchMembers"/>.
/// </summary>
[KernType("regex_match")]
public sealed class KernRegexMatch
{
	private KernRegexMatch(string text, int start, int end, string[] groups, Dictionary<string, string> named)
	{
		Text = text;
		Start = start;
		End = end;
		Groups = groups;
		Named = named;
	}

	/// <summary>Совпавший текст (группа 0).</summary>
	public string Text { get; }

	/// <summary>Начало совпадения в исходной строке.</summary>
	public int Start { get; }

	/// <summary>Конец совпадения (не включая).</summary>
	public int End { get; }

	/// <summary>Все группы: 0 — совпадение целиком, далее по порядку.</summary>
	public string[] Groups { get; }

	/// <summary>Именованные группы.</summary>
	public Dictionary<string, string> Named { get; }

	internal static KernRegexMatch From(NetMatch match)
	{
		var groups = new string[match.Groups.Count];
		for (var index = 0; index < groups.Length; index++)
			groups[index] = match.Groups[index].Value;

		var named = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (string name in match.Groups.Keys)
			if (!int.TryParse(name, out _))
				named[name] = match.Groups[name].Value;

		return new KernRegexMatch(match.Value, match.Index, match.Index + match.Length, groups, named);
	}
}

/// <summary>Члены <c>regex_match</c>: <c>text/start/end/len/groups</c> и <c>group(name|index)</c>.</summary>
[KernType("regex_match")]
public static class RegexMatchMembers
{
	/// <summary><c>text</c>: совпавший текст.</summary>
	[KernProperty]
	public static string Text(KNativeBox self) => Require(self).Text;

	/// <summary><c>start</c>: начало совпадения.</summary>
	[KernProperty]
	public static int Start(KNativeBox self) => Require(self).Start;

	/// <summary><c>end</c>: конец совпадения (не включая).</summary>
	[KernProperty]
	public static int End(KNativeBox self) => Require(self).End;

	/// <summary><c>len</c>: длина совпадения.</summary>
	[KernProperty]
	public static int Len(KNativeBox self) => Require(self).End - Require(self).Start;

	/// <summary><c>groups</c>: значения групп 1..N (без полного совпадения).</summary>
	[KernProperty]
	public static KList<string> Groups(KNativeBox self)
	{
		KernRegexMatch match = Require(self);
		return new KList<string>(KValue.Obj(new Kern.Vm.Values.KList(
			null,
			[.. match.Groups.Skip(1).Select(KValue.Str)])));
	}

	/// <summary><c>group(name)</c>: значение именованной группы или <c>nil</c>.</summary>
	[KernMember]
	public static string? Group(KNativeBox self, string name) =>
		Require(self).Named.TryGetValue(name, out string? value) ? value : null;

	/// <summary><c>group(index)</c>: значение группы по номеру или <c>nil</c>.</summary>
	[KernMember]
	public static string? Group(KNativeBox self, int index)
	{
		string[] groups = Require(self).Groups;
		return index >= 0 && index < groups.Length ? groups[index] : null;
	}

	private static KernRegexMatch Require(KNativeBox self) => self.Value switch
	{
		KernRegexMatch match => match,
		_ => throw new KernPanic($"ожидался regex_match, получено {self.Value.GetType().Name}"),
	};
}
