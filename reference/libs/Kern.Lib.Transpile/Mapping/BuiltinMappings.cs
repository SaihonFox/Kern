namespace Kern.Lib.Transpile.Mapping;

/// <summary>Встроенные соответствия самых частых членов std (print/println, str, list, dict). Остальное — по именам.</summary>
public sealed class BuiltinMappings : IMappings
{
	// "владелец.имя" → (C#, Kotlin). {0} — получатель, {1}.. — аргументы, {*} — все аргументы через запятую.
	private static readonly Dictionary<string, (string Cs, string Kt)> Table = new()
	{
		[".println"] = ("Console.WriteLine({*})", "println({*})"),
		[".print"] = ("Console.Write({*})", "print({*})"),
		[".eprintln"] = ("Console.Error.WriteLine({*})", "System.err.println({*})"),
		[".panic"] = ("throw new InvalidOperationException({1})", "throw IllegalStateException({1})"),
		[".exit"] = ("Environment.Exit({*})", "kotlin.system.exitProcess({*})"),
		[".delay"] = ("Task.Delay({1})", "kotlinx.coroutines.delay(({1}).toLong())"),

		["str.len"] = ("{0}.Length", "{0}.length"),
		["str.is_empty"] = ("({0}.Length == 0)", "{0}.isEmpty()"),
		["str.upper"] = ("{0}.ToUpperInvariant()", "{0}.uppercase()"),
		["str.lower"] = ("{0}.ToLowerInvariant()", "{0}.lowercase()"),
		["str.trim"] = ("{0}.Trim()", "{0}.trim()"),
		["str.contains"] = ("{0}.Contains({1})", "{0}.contains({1})"),
		["str.starts_with"] = ("{0}.StartsWith({1})", "{0}.startsWith({1})"),
		["str.ends_with"] = ("{0}.EndsWith({1})", "{0}.endsWith({1})"),
		["str.replace"] = ("{0}.Replace({1}, {2})", "{0}.replace({1}, {2})"),
		["str.index_of"] = ("{0}.IndexOf({1})", "{0}.indexOf({1})"),

		["list.len"] = ("{0}.Count", "{0}.size"),
		["list.is_empty"] = ("({0}.Count == 0)", "{0}.isEmpty()"),
		["list.first"] = ("{0}[0]", "{0}.first()"),
		["list.last"] = ("{0}[^1]", "{0}.last()"),
		["list.add"] = ("{0}.Add({1})", "{0}.add({1})"),
		["list.insert"] = ("{0}.Insert({1}, {2})", "{0}.add({1}, {2})"),
		["list.remove"] = ("{0}.Remove({1})", "{0}.remove({1})"),
		["list.remove_at"] = ("{0}.RemoveAt({1})", "{0}.removeAt({1})"),
		["list.clear"] = ("{0}.Clear()", "{0}.clear()"),
		["list.contains"] = ("{0}.Contains({1})", "{0}.contains({1})"),
		["list.index_of"] = ("{0}.IndexOf({1})", "{0}.indexOf({1})"),
		["list.reverse"] = ("{0}.Reverse()", "{0}.reverse()"),
		["list.sort"] = ("{0}.Sort()", "{0}.sort()"),
		["list.join"] = ("string.Join({1}, {0})", "{0}.joinToString({1})"),

		["array.len"] = ("{0}.Length", "{0}.size"),

		["dict.len"] = ("{0}.Count", "{0}.size"),
		["dict.is_empty"] = ("({0}.Count == 0)", "{0}.isEmpty()"),
		["dict.contains_key"] = ("{0}.ContainsKey({1})", "{0}.containsKey({1})"),
		["dict.keys"] = ("{0}.Keys", "{0}.keys"),
		["dict.values"] = ("{0}.Values", "{0}.values"),
		["dict.remove"] = ("{0}.Remove({1})", "{0}.remove({1})"),
	};

	private static readonly Dictionary<string, (string Cs, string Kt)> Types = new()
	{
		["duration"] = ("TimeSpan", "kotlin.time.Duration"),
		["datetime"] = ("DateTime", "java.time.LocalDateTime"),
		["uuid"] = ("Guid", "java.util.UUID"),
		["regex"] = ("System.Text.RegularExpressions.Regex", "Regex"),
		["range"] = ("IEnumerable<long>", "LongRange"),
	};

	public string? MapType(string target, string kernType, IReadOnlyList<string> args)
	{
		if (!Types.TryGetValue(kernType, out var t)) return null;
		return target == "kotlin" ? t.Kt : t.Cs;
	}

	public string? MapCall(string target, string owner, string name, string receiver, IReadOnlyList<string> args) =>
		Apply(target, owner, name, receiver, args);

	public string? MapMember(string target, string owner, string name, string receiver) =>
		Apply(target, owner, name, receiver, []);

	private static string? Apply(string target, string owner, string name, string receiver, IReadOnlyList<string> args)
	{
		if (!Table.TryGetValue($"{owner}.{name}", out var t)) return null;
		var tpl = target == "kotlin" ? t.Kt : t.Cs;
		var s = tpl.Replace("{*}", string.Join(", ", args)).Replace("{0}", receiver);
		for (int i = args.Count; i >= 1; i--) s = s.Replace("{" + i + "}", args[i - 1]);
		return s;
	}
}
