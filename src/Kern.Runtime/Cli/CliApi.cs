using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Cli;

/// <summary>
/// Описание одной опции парсера: длинное имя, короткое имя (<c>""</c> — нет),
/// признак «нужно значение» и справка.
/// </summary>
internal sealed record CliOption(string Long, string Short, bool HasValue, string Default, string Help);

/// <summary>
/// <c>cli_parser</c> (spec §16, раунд 27): объявленные флаги и опции.
/// </summary>
[KernType("cli_parser")]
public sealed class KernCliParser
{
	private readonly Dictionary<string, CliOption> _byLong = new(StringComparer.Ordinal);
	private readonly Dictionary<string, CliOption> _byShort = new(StringComparer.Ordinal);

	internal KernCliParser(string name, string description)
	{
		Name = name;
		Description = description;
	}

	internal string Name { get; }

	internal string Description { get; }

	internal IReadOnlyList<CliOption> Options => [.. _byLong.Values];

	internal void Add(string longName, string shortName, bool hasValue, string defaultValue, string help)
	{
		string longKey = TrimDashes(longName);
		if (longKey.Length == 0)
			throw new KernPanic("cli: пустое длинное имя опции");

		string shortKey = TrimDashes(shortName);
		if (shortKey.Length > 1)
			throw new KernPanic($"cli: короткое имя '{shortName}' должно быть одним символом");

		if (_byLong.ContainsKey(longKey))
			throw new KernPanic($"cli: опция '--{longKey}' уже объявлена");

		var option = new CliOption(longKey, shortKey, hasValue, defaultValue, help ?? string.Empty);
		_byLong.Add(longKey, option);
		if (shortKey.Length > 0)
		{
			if (_byShort.ContainsKey(shortKey))
				throw new KernPanic($"cli: короткая опция '-{shortKey}' уже объявлена");
			_byShort.Add(shortKey, option);
		}
	}

	internal CliOption? FindLong(string name) =>
		_byLong.TryGetValue(TrimDashes(name), out CliOption? option) ? option : null;

	internal CliOption? FindShort(string name) =>
		_byShort.TryGetValue(name, out CliOption? option) ? option : null;

	internal static string TrimDashes(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return string.Empty;
		string trimmed = value.Trim();
		return trimmed.TrimStart('-');
	}
}

/// <summary>
/// <c>cli_values</c> (spec §16): результат разбора. Хранит явно заданные значения,
/// позиционные аргументы и признак запрошенной справки.
/// </summary>
[KernType("cli_values")]
public sealed class KernCliValues
{
	internal HashSet<string> Present { get; } = new(StringComparer.Ordinal);

	internal Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

	internal Dictionary<string, string> Defaults { get; } = new(StringComparer.Ordinal);

	internal List<string> Positionals { get; } = [];

	internal bool HelpRequested { get; set; }
}

/// <summary>Члены <c>cli_parser</c>; первый параметр — <c>self</c>.</summary>
[KernType("cli_parser")]
public static class CliParserMembers
{
	/// <summary><c>p.flag(long, short, help)</c>: объявить флаг без значения.</summary>
	[KernMember]
	public static void Flag(KNativeBox self, string longName, string shortName, string help) =>
		Require(self).Add(longName, shortName, hasValue: false, string.Empty, help);

	/// <summary><c>p.option(long, short, default, help)</c>: объявить опцию со значением.</summary>
	[KernMember]
	public static void Option(KNativeBox self, string longName, string shortName, string defaultValue, string help) =>
		Require(self).Add(longName, shortName, hasValue: true, defaultValue, help);

	/// <summary><c>p.usage()</c>: строка справки в стиле <c>Usage: name [options] …</c>.</summary>
	[KernMember]
	public static string Usage(KNativeBox self)
	{
		KernCliParser parser = Require(self);
		var builder = new StringBuilder();
		builder.Append("Usage: ").Append(parser.Name).Append(" [options] [positional...]");
		if (parser.Description.Length > 0)
			builder.Append('\n').Append(parser.Description);
		foreach (CliOption option in parser.Options)
		{
			builder.Append("\n  --").Append(option.Long);
			if (option.HasValue)
				builder.Append(" <value>");
			if (option.Short.Length > 0)
				builder.Append(", -").Append(option.Short);
			if (option.Help.Length > 0)
				builder.Append("\t").Append(option.Help);
			if (option.HasValue && option.Default.Length > 0)
				builder.Append(" (по умолчанию: ").Append(option.Default).Append(')');
		}

		return builder.ToString();
	}

	/// <summary><c>p.parse(args) -&gt; result&lt;cli_values, str&gt;</c>.</summary>
	[KernMember]
	public static KResult<KernCliValues, string> Parse(KNativeBox self, KList<string> arguments)
	{
		KernCliParser parser = Require(self);
		var values = new KernCliValues();
		foreach (CliOption declared in parser.Options)
			if (declared.HasValue)
				values.Defaults[declared.Long] = declared.Default;
		var options = true;
		string[] args = arguments.ToArray();
		for (var index = 0; index < args.Length; index++)
		{
			string argument = args[index];
			if (options && argument == "--")
			{
				options = false;
				continue;
			}

			if (options && argument.StartsWith("--", StringComparison.Ordinal))
			{
				string body = argument[2..];
				int equals = body.IndexOf('=');
				string name = equals >= 0 ? body[..equals] : body;
				string? inline = equals >= 0 ? body[(equals + 1)..] : null;
				if (name == "help")
				{
					values.HelpRequested = true;
					continue;
				}

				CliOption? option = parser.FindLong(name);
				if (option is null)
					return Fail($"cli: неизвестная опция '--{name}'");
				if (option.HasValue)
				{
					string? value = inline;
					if (value is null)
					{
						if (index + 1 >= args.Length)
							return Fail($"cli: опции '--{name}' нужно значение");
						value = args[++index];
					}

					values.Present.Add(option.Long);
					values.Values[option.Long] = value;
					continue;
				}

				if (inline is not null)
					return Fail($"cli: флаг '--{name}' не принимает значение");
				if (!values.Present.Add(option.Long))
					return Fail($"cli: флаг '--{name}' указан повторно");
				continue;
			}

			if (options && argument.Length > 1 && argument[0] == '-')
			{
				string body = argument[1..];
				string shortName = body[..1];
				if (shortName == "h" && body.Length == 1)
				{
					values.HelpRequested = true;
					continue;
				}

				CliOption? option = parser.FindShort(shortName);
				if (option is null)
					return Fail($"cli: неизвестная опция '-{shortName}'");
				if (option.HasValue)
				{
					string value;
					if (body.Length > 1)
						value = body[1..];
					else if (index + 1 < args.Length)
						value = args[++index];
					else
						return Fail($"cli: опции '-{shortName}' нужно значение");
					values.Present.Add(option.Long);
					values.Values[option.Long] = value;
					continue;
				}

				if (body.Length > 1)
					return Fail($"cli: флаг '-{shortName}' не принимает значение");
				if (!values.Present.Add(option.Long))
					return Fail($"cli: флаг '-{shortName}' указан повторно");
				continue;
			}

			values.Positionals.Add(argument);
		}

		return KResult<KernCliValues, string>.Success(values);
	}

	private static KResult<KernCliValues, string> Fail(string message) =>
		KResult<KernCliValues, string>.Failure(message);

	private static KernCliParser Require(KNativeBox self) => self.Value switch
	{
		KernCliParser parser => parser,
		_ => throw new KernPanic($"cli_parser: ожидался парсер, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Члены <c>cli_values</c>; первый параметр — <c>self</c>.</summary>
[KernType("cli_values")]
public static class CliValuesMembers
{
	/// <summary><c>v.flag(name)</c>: был ли указан флаг.</summary>
	[KernMember]
	public static bool Flag(KNativeBox self, string name)
	{
		KernCliValues values = Require(self);
		string key = KernCliParser.TrimDashes(name);
		return values.Present.Contains(key) && !values.Values.ContainsKey(key);
	}

	/// <summary><c>v.option(name)</c>: значение опции или её значение по умолчанию.</summary>
	[KernMember]
	public static string Option(KNativeBox self, string name)
	{
		KernCliValues values = Require(self);
		string key = KernCliParser.TrimDashes(name);
		if (values.Values.TryGetValue(key, out string? value))
			return value;
		return values.Defaults.TryGetValue(key, out string? fallback) ? fallback : string.Empty;
	}

	/// <summary><c>v.positional()</c>: позиционные аргументы в порядке появления.</summary>
	[KernMember]
	public static KList<string> Positional(KNativeBox self) =>
		Strings(Require(self).Positionals);

	/// <summary><c>v.has(name)</c>: опция или флаг были указаны явно.</summary>
	[KernMember]
	public static bool Has(KNativeBox self, string name) =>
		Require(self).Present.Contains(KernCliParser.TrimDashes(name));

	/// <summary><c>v.help_requested</c>: встретились ли <c>-h</c>/<c>--help</c>.</summary>
	[KernName("help_requested")]
	[KernProperty]
	public static bool HelpRequested(KNativeBox self) => Require(self).HelpRequested;

	private static KernCliValues Require(KNativeBox self) => self.Value switch
	{
		KernCliValues values => values,
		_ => throw new KernPanic($"cli_values: ожидался результат разбора, получено {self.Value.GetType().Name}"),
	};

	internal static KList<string> Strings(IEnumerable<string> items) =>
		new(KValue.Obj(new Kern.Vm.Values.KList(null, [.. items.Select(KValue.Str)])));
}

/// <summary>Модуль <c>kern.cli</c>: аргументы процесса и фабрика парсера.</summary>
[KernModule("kern.cli")]
public static class CliApi
{
	/// <summary><c>cli.args() -&gt; list&lt;str&gt;</c>: аргументы без имени программы.</summary>
	[KernMember]
	public static KList<string> Args() =>
		CliValuesMembers.Strings(Environment.GetCommandLineArgs().Skip(1));

	/// <summary><c>cli.parser(name, description)</c>: новый парсер.</summary>
	[KernMember]
	public static KernCliParser Parser(string name, string description) =>
		new(name, description);
}
